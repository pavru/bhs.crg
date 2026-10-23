using BHS.CRG.Domain.Common;

namespace BHS.CRG.Modules.Plan.Data;

/// <summary>Вид сметы стройки (ТЗ PLAN-1, PLAN-5.2): основная или дополнительная.</summary>
public enum EstimateKind
{
    Main,
    Additional,
}

/// <summary>
/// Смета стройки или её раздела (change <c>plan-06-estimate-model</c>, ТЗ PLAN-1, D3 зонтика
/// <c>plan-00-epic</c>). Сама смета — только шапка: строки живут в её редакциях
/// (<see cref="EstimateRevision" />), и редакций у сметы может быть много.
///
/// <para><b>Действующая редакция — одна на смету</b> (ТЗ PLAN-5.2): план стройки — сумма действующих
/// редакций всех её смет, а не всех редакций. Первая редакция становится действующей сама; следующие —
/// только действием человека (решение В-5): повторный импорт не должен молча менять план, из которого
/// уже собраны КП и график.</para>
///
/// <para>⚠️ Ссылка на действующую редакцию держится и базой — внешним ключом без каскада: редакцию,
/// которая действует, база не даст удалить и одновременному запросу, проверка которого прошла раньше
/// смены действующей.</para>
/// </summary>
public sealed class Estimate
{
    /// <summary>Предел длины названия — то же число, что у колонки базы.</summary>
    public const int NameLength = 300;

    private Estimate() { }

    public Guid Id { get; private set; }

    /// <summary>Стройка — запись ядра; идентификатором, без внешнего ключа: схемы ядра и модуля ключами
    /// не связываются (ТЗ CORE-34.1).</summary>
    public Guid ConstructionId { get; private set; }

    /// <summary>Раздел стройки, если смета составлена на раздел; <c>null</c> — на стройку целиком.</summary>
    public Guid? SectionId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public EstimateKind Kind { get; private set; }

    /// <summary>Действующая редакция; <c>null</c> — редакций ещё нет.</summary>
    public Guid? CurrentRevisionId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Estimate Create(Guid constructionId, Guid? sectionId, string name, EstimateKind kind, DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(),
            ConstructionId = constructionId,
            SectionId = sectionId,
            Name = CheckedName(name),
            Kind = kind,
            CreatedAt = now,
        };

    /// <summary>Новое название и, если назван, вид; не названный вид остаётся прежним.</summary>
    public void Rename(string name, EstimateKind? kind)
    {
        Name = CheckedName(name);
        Kind = kind ?? Kind;
    }

    /// <summary>
    /// Сделать редакцию действующей. Редакция обязана быть этой сметы — иначе план стройки считался бы
    /// по строкам чужой сметы, а то и чужой стройки.
    /// </summary>
    public void MakeCurrent(EstimateRevision revision)
    {
        if (revision.EstimateId != Id)
            throw new InvalidRequestException(
                $"Редакция {revision.Number} относится к другой смете — действующей у сметы «{Name}» она быть не может.");
        CurrentRevisionId = revision.Id;
    }

    private static string CheckedName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) throw new InvalidRequestException("У сметы нет названия — по нему её находят в списке стройки.");
        if (trimmed.Length > NameLength)
            throw new InvalidRequestException($"Название сметы длиннее {NameLength} знаков.");
        return trimmed;
    }
}
