using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Work.Data;

/// <summary>
/// Строка работы отчёта (ТЗ WORK-5, WORK-50; change <c>work-13-report-model</c>): что сделано — вид работы,
/// вариант, объём с единицей, где — раздел стройки и место, и кем — признак бригадной работы.
///
/// <para><b>Колонками, а не JSON.</b> На эти поля опираются код и сверки: позиция перечня строится по виду,
/// разделу и единице (ТЗ CORE-12), журнал работ — по месту и материалам, бригада — по признаку. Сведения сверх
/// этого (испытания, условия, поля заказчика) лежат в <see cref="Details" /> по схеме типа
/// <c>СтрокаРаботыСведения</c> и проходят охрану записи.</para>
///
/// <para><b>Позиция перечня</b> (<see cref="WorkPlanItemId" />) ставится сервером по ключу строки через порт этапа 3
/// (<see cref="WorkPlanItemLink" />). Подсказка клиента ключом не служит.</para>
///
/// <para>Изолированная, как и отчёт: строка несёт то же, что отчёт, и <c>Set&lt;ReportLine&gt;()</c> в чужом
/// файле читал бы чужие работы.</para>
/// </summary>
public sealed class ReportLine : IIsolatedRecord
{
    private ReportLine() { }

    /// <summary>Идентификатор — от клиента (ТЗ MOB-15), как у отчёта.</summary>
    public Guid Id { get; private set; }

    public Guid ReportId { get; private set; }

    /// <summary>Порядок строки в отчёте — с нуля.</summary>
    public int Position { get; private set; }

    /// <summary>Вид работы — запись классификатора ядра «ВидРаботы».</summary>
    public Guid WorkTypeId { get; private set; }

    /// <summary>Вариант своего вида работы (ТЗ WORK-49) или <c>null</c>.</summary>
    public Guid? VariantId { get; private set; }

    public decimal Volume { get; private set; }

    /// <summary>Единица объёма — запись справочника ядра «ЕдиницаИзмерения».</summary>
    public Guid UnitId { get; private set; }

    /// <summary>Раздел стройки отчёта или <c>null</c> — «стройка в целом».</summary>
    public Guid? SectionId { get; private set; }

    /// <summary>Место работ словами: «оси 3–4, отм. +6.000».</summary>
    public string? Location { get; private set; }

    public string? Comment { get; private set; }

    /// <summary>Бригадная работа: участники перечисляются только у неё (ТЗ WORK-50).</summary>
    public bool IsCrew { get; private set; }

    /// <summary>Позиция перечня работ стройки — ставит сервер по ключу строки (ТЗ CORE-12).</summary>
    public Guid WorkPlanItemId { get; private set; }

    /// <summary>Сведения строки — JSON по схеме типа <c>СтрокаРаботыСведения</c>; пусто — <c>{}</c>.</summary>
    public string Details { get; private set; } = "{}";

    public List<ReportLineMaterial> Materials { get; private set; } = [];

    public List<ReportLineParticipant> Participants { get; private set; } = [];

    /// <summary>Фото строки (<c>work-17</c>): объявленные и пришедшие.</summary>
    public List<LinePhoto> Photos { get; private set; } = [];

    /// <summary>Ключ позиции перечня этой строки на стройке отчёта.</summary>
    public PlanItemKey KeyOn(Guid constructionId) => new(WorkTypeId, constructionId, SectionId, UnitId);

    /// <summary>
    /// Принять перечень фото строки (ТЗ WORK-34, <c>work-17</c>): новое — ждёт файла, не названное — снимается.
    /// <c>null</c> — перечень не прислан, фото не трогаются: правка без знания о фото их не теряет. Возвращает снятые —
    /// их файлы удаляются после фиксации, ссылка уходит первой.
    /// </summary>
    public IReadOnlyList<LinePhoto> ApplyPhotos(IReadOnlyList<Guid>? named, DateTimeOffset now)
    {
        if (named is null) return [];
        var keep = named.ToHashSet();
        var removed = Photos.Where(p => !keep.Contains(p.Id)).ToList();
        Photos.RemoveAll(p => !keep.Contains(p.Id));
        var have = Photos.Select(p => p.Id).ToHashSet();
        Photos.AddRange(named.Distinct().Where(id => !have.Contains(id)).Select(id => LinePhoto.Declared(id, Id, now)));
        return removed;
    }

    /// <summary>
    /// Новая строка. Звать — только из пути записи (<see cref="ReportWriter" />): проверки и охрана сведений там, а
    /// перепись охраны (<c>RecordWriteGuardCoverageTests</c>) находит место записи по имени метода.
    /// </summary>
    public static ReportLine Create(Guid reportId, int position, ReportLineValues values, Guid workPlanItemId)
    {
        var line = new ReportLine { Id = values.Id, ReportId = reportId };
        line.Set(position, values, workPlanItemId);
        return line;
    }

    /// <summary>
    /// Принять значения строки. Проверки — до вызова (<see cref="ReportRules" />, ссылки, позиция перечня, охрана
    /// сведений): здесь только перенос, чтобы строка никогда не стояла наполовину правленой. Имя неповторимо —
    /// по нему перепись охраны находит место записи сведений.
    /// </summary>
    public void ApplyLine(int position, ReportLineValues values, Guid workPlanItemId) => Set(position, values, workPlanItemId);

    private void Set(int position, ReportLineValues values, Guid workPlanItemId)
    {
        Position = position;
        WorkTypeId = values.WorkTypeId;
        VariantId = values.VariantId;
        Volume = values.Volume;
        UnitId = values.UnitId;
        SectionId = values.SectionId;
        Location = ReportRules.Text(values.Location);
        Comment = ReportRules.Text(values.Comment);
        IsCrew = values.IsCrew;
        WorkPlanItemId = workPlanItemId;
        Details = string.IsNullOrWhiteSpace(values.Details) ? "{}" : values.Details;

        // Материалы и участники пересобираются, только когда изменились: правка объёма не переписывает их строки.
        var materials = values.Materials.Select(m => (m.NomenclatureId!.Value, ReportRules.Text(m.Note))).ToList();
        if (!Materials.OrderBy(m => m.Position).Select(m => (m.NomenclatureId, m.Note)).SequenceEqual(materials))
        {
            Materials.Clear();
            Materials.AddRange(materials.Select((m, i) => ReportLineMaterial.Create(Id, i, m.Item1, m.Item2)));
        }
        var participants = values.Participants.Distinct().ToHashSet();
        if (!Participants.Select(p => p.EmployeeId).ToHashSet().SetEquals(participants))
        {
            Participants.Clear();
            Participants.AddRange(participants.Select(e => ReportLineParticipant.Create(Id, e)));
        }
    }
}

/// <summary>Материал строки во входе: ссылка на номенклатуру обязательна, текст — только в примечании (ТЗ WORK-50).</summary>
public sealed record ReportMaterialValues(Guid? NomenclatureId, string? Note);

/// <summary>Значения строки, разобранные из запроса, — то, что проверяют правила и что принимает строка.</summary>
public sealed record ReportLineValues(
    Guid Id, Guid WorkTypeId, Guid? VariantId, decimal Volume, Guid UnitId, Guid? SectionId,
    string? Location, string? Comment, bool IsCrew, string? Details,
    IReadOnlyList<ReportMaterialValues> Materials, IReadOnlyList<Guid> Participants, IReadOnlyList<Guid>? Photos = null)
{
    /// <summary>Ключ позиции перечня строки на стройке.</summary>
    public PlanItemKey KeyOn(Guid constructionId) => new(WorkTypeId, constructionId, SectionId, UnitId);
}

/// <summary>Применённый материал строки — ссылка на номенклатуру ядра и примечание.</summary>
public sealed class ReportLineMaterial : IIsolatedRecord
{
    private ReportLineMaterial() { }

    /// <summary>Свой ключ, а не «строка + порядок»: правка строки пересобирает материалы целиком, и прежний ключ у
    /// нового материала столкнулся бы в одном сохранении с удаляемым.</summary>
    public Guid Id { get; private set; }

    public Guid LineId { get; private set; }

    /// <summary>Порядок материала в строке — с нуля.</summary>
    public int Position { get; private set; }

    /// <summary>Запись справочника номенклатуры ядра (роль <c>role.nomenclature</c>).</summary>
    public Guid NomenclatureId { get; private set; }

    /// <summary>Свободный текст — только здесь: «остаток бухты», «с объекта Б».</summary>
    public string? Note { get; private set; }

    public static ReportLineMaterial Create(Guid lineId, int position, Guid nomenclatureId, string? note) => new()
    {
        Id = Guid.NewGuid(), LineId = lineId, Position = position, NomenclatureId = nomenclatureId,
        Note = ReportRules.Text(note),
    };
}

/// <summary>Участник бригадной строки — сотрудник справочника ядра. Правила участия — <c>work-18</c>.</summary>
public sealed class ReportLineParticipant : IIsolatedRecord
{
    private ReportLineParticipant() { }

    /// <summary>Свой ключ — по той же причине, что у <see cref="ReportLineMaterial.Id" />.</summary>
    public Guid Id { get; private set; }

    public Guid LineId { get; private set; }

    public Guid EmployeeId { get; private set; }

    public static ReportLineParticipant Create(Guid lineId, Guid employeeId) => new() { Id = Guid.NewGuid(), LineId = lineId, EmployeeId = employeeId };
}
