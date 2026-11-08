using System.Text.Json;

namespace BHS.CRG.Modules.Plan.Data;

/// <summary>
/// Категория позиции отчёта переноса (change <c>plan-10-distribution</c>, «Перенос» в <c>design.md</c>).
/// <c>carried</c> позицией не хранится — только числом в заголовке: решать о ней нечего.
/// </summary>
public enum CarryOverCategory
{
    /// <summary>Части перенесены, но номер в разделе сменился или раздел сопоставлен по номеру главы — «проверьте».</summary>
    Shifted,

    /// <summary>Число строк с шифром в разделе другое: части не перенесены, есть строки-кандидаты.</summary>
    Ambiguous,

    /// <summary>Строка сопоставлена, но прежние части больше её нового объёма: не перенесены (ТЗ COST-13).</summary>
    Overflow,

    /// <summary>Раздел не сопоставлен или шифра в нём больше нет: не перенесено.</summary>
    Lost,
}

/// <summary>Что из источника не перенесено как есть: части строки или правило раздела ЛСР.</summary>
public enum CarryOverSubject
{
    Row,
    Rule,
}

/// <summary>Чем решена позиция отчёта переноса.</summary>
public enum CarryOverResolution
{
    /// <summary>Прежние части записаны на строку-кандидат.</summary>
    Applied,

    /// <summary>«Не переносить».</summary>
    Skipped,

    /// <summary>«Понятно».</summary>
    Acknowledged,
}

/// <summary>Прежняя часть строки источника — в единице строки, как хранилась.</summary>
public sealed record CarriedPart(Guid SectionId, decimal Quantity);

/// <summary>
/// Позиция отчёта переноса распределения у новой редакции (change <c>plan-10-distribution</c>, ТЗ PLAN-5.1):
/// то, что перенесено с пометкой или не перенесено вовсе. Хранится, пока по ней не решили, — повторный импорт не
/// теряет работу молча и не путает её.
///
/// <para>Источник запомнен словами и числами, а не ссылкой: редакцию-источник могут удалить, а позиция обязана
/// по-прежнему сказать, что за строка и какие у неё были части.</para>
/// </summary>
public sealed class CarryOverItem
{
    public const int SectionNameLength = EstimateRow.SectionNameLength;

    private CarryOverItem() { }

    public Guid Id { get; private set; }

    /// <summary>Новая редакция — та, у которой отчёт.</summary>
    public Guid RevisionId { get; private set; }

    public CarryOverCategory Category { get; private set; }

    public CarryOverSubject Subject { get; private set; }

    /// <summary>Строка редакции-источника — справка: источник могут удалить.</summary>
    public Guid? SourceRowId { get; private set; }

    public string? LsrSectionCode { get; private set; }

    public string? LsrSectionName { get; private set; }

    public string? RateCode { get; private set; }

    public string? NumberInSection { get; private set; }

    public string? Number { get; private set; }

    public string? Name { get; private set; }

    public decimal? SourceVolume { get; private set; }

    public string? SourceUnit { get; private set; }

    /// <summary>Прежние части строки — JSON-массив <see cref="CarriedPart" />; у правила — пустой.</summary>
    public string Parts { get; private set; } = "[]";

    /// <summary>Цель правила раздела источника — у позиции-правила.</summary>
    public DistributionTargetKind? RuleKind { get; private set; }

    public Guid? RuleSectionId { get; private set; }

    /// <summary>Строка новой редакции, с которой позиция сопоставлена (<c>shifted</c>, <c>overflow</c>).</summary>
    public Guid? RowId { get; private set; }

    /// <summary>Новый объём сопоставленной строки — у <c>overflow</c>.</summary>
    public decimal? NewVolume { get; private set; }

    /// <summary>Строки-кандидаты новой редакции — у <c>ambiguous</c>.</summary>
    public Guid[] Candidates { get; private set; } = [];

    public CarryOverResolution? Resolution { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    /// <summary>Ждёт решения: <c>ambiguous</c>, <c>overflow</c> и <c>lost</c>, пока не решены.</summary>
    public bool Pending => Resolution is null && Category != CarryOverCategory.Shifted;

    public IReadOnlyList<CarriedPart> PartList =>
        JsonSerializer.Deserialize<List<CarriedPart>>(Parts, Json) ?? [];

    internal static CarryOverItem Create(Guid revisionId, CarryOverCategory category, CarryOverSubject subject,
        CarryOverSource source, IReadOnlyList<CarriedPart> parts, DistributionTarget? rule, Guid? rowId,
        decimal? newVolume, IReadOnlyList<Guid> candidates) =>
        new()
        {
            Id = Guid.NewGuid(),
            RevisionId = revisionId,
            Category = category,
            Subject = subject,
            SourceRowId = source.RowId,
            LsrSectionCode = PlanText.Clip(source.LsrSectionCode, EstimateRow.SectionCodeLength),
            LsrSectionName = PlanText.Clip(source.LsrSectionName, SectionNameLength),
            RateCode = PlanText.Clip(source.RateCode, EstimateRow.RateCodeLength),
            NumberInSection = PlanText.Clip(source.NumberInSection, EstimateRow.NumberLength),
            Number = PlanText.Clip(source.Number, EstimateRow.NumberLength),
            Name = source.Name,
            SourceVolume = source.SourceVolume,
            SourceUnit = PlanText.Clip(source.SourceUnit, EstimateRow.UnitLength),
            Parts = JsonSerializer.Serialize(parts, Json),
            RuleKind = rule?.Kind,
            RuleSectionId = rule?.SectionId,
            RowId = rowId,
            NewVolume = newVolume,
            Candidates = [.. candidates],
        };

    internal void Resolve(CarryOverResolution resolution, DateTimeOffset now)
    {
        Resolution = resolution;
        ResolvedAt = now;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>Чем позиция отчёта называет строку или раздел источника.</summary>
public sealed record CarryOverSource(
    Guid? RowId, string? LsrSectionCode, string? LsrSectionName, string? RateCode = null, string? NumberInSection = null,
    string? Number = null, string? Name = null, decimal? SourceVolume = null, string? SourceUnit = null);
