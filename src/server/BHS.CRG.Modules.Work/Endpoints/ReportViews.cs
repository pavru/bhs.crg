using System.Text.Json;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;

namespace BHS.CRG.Modules.Work.Endpoints;

public sealed record ReportMaterialDto(Guid NomenclatureId, string? Note);

public sealed record ReportLineDto(
    Guid Id, int Position, Guid WorkTypeId, Guid? VariantId, decimal Volume, Guid UnitId, Guid? SectionId,
    string? Location, string? Comment, bool IsCrew, Guid WorkPlanItemId, JsonElement Details,
    IReadOnlyList<ReportMaterialDto> Materials, IReadOnlyList<Guid> Participants);

/// <summary>Кто ввёл отчёт за сотрудника (ТЗ WORK-31): учётная запись и имя снимком на момент заведения.</summary>
public sealed record ReportEnteredByDto(Guid UserId, string Name);

/// <summary>
/// Отчёт в ответе (ТЗ WORK-4, WORK-31, MOB-15, MOB-16, MOB-21).
/// </summary>
/// <param name="Seq">Номер последнего изменения в серверной последовательности — по нему курсор <c>since</c>.</param>
/// <param name="Version">Значение для <c>If-Match</c> правки и удаления.</param>
/// <param name="AssignedOnDate">Назначен ли сотрудник на стройку на дату отчёта (MOB-21) — пометка, не отказ.</param>
/// <param name="EnteredBy">Автор ввода за сотрудника; <c>null</c> — отчёт завёл сам сотрудник.</param>
/// <param name="Replayed">Ответ — повтор уже применённого запроса (MOB-15): ничего не записано второй раз.</param>
/// <param name="AutoSubmitAt">Конец окна подачи у черновика (WORK-10, WORK-54) — когда отчёт подастся сам; у
/// остальных статусов <c>null</c>: возвращённый автоподача не трогает. Клиент момента не вычисляет.</param>
/// <param name="SubmittedAt">Момент подачи; <c>null</c> — отчёт не подавался.</param>
/// <param name="SubmittedBy"><c>manual</c> | <c>auto</c>; <c>null</c> — не подавался.</param>
/// <param name="RequiredMissing">Что не заполнено к автоподаче (WORK-6): <c>[{ line, lineId, field }]</c>;
/// <c>null</c> — всё заполнено или подал человек.</param>
/// <param name="AlreadySubmitted">Ответ подачи — отчёт уже был подан (автоподача обогнала запрос): второй подачи
/// нет.</param>
/// <param name="AcceptWouldMark">Отчёт подан, и ввёл его тот, кто сейчас смотрит (WORK-31.1, <c>work-16</c>): его
/// приёмка пометит отчёт «ввёл и принял один человек». Считает сервер — клиент о пометке не догадывается.</param>
/// <param name="Marks">Пометки отчёта из каталога очереди (WORK-28, <c>work-16</c>) — те же, что у строки очереди.</param>
public sealed record ReportDto(
    Guid Id, Guid EmployeeId, Guid ConstructionId, DateOnly Date, string Shift, decimal? Hours, string Status,
    string? Note, long Seq, string Version, bool AssignedOnDate, ReportEnteredByDto? EnteredBy,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<ReportLineDto> Lines, bool Replayed,
    DateTimeOffset? AutoSubmitAt, DateTimeOffset? SubmittedAt, string? SubmittedBy, JsonElement? RequiredMissing,
    bool AlreadySubmitted, bool AcceptWouldMark, IReadOnlyList<ReviewMarkDto> Marks);

/// <summary>
/// Отчёты в ответах адресов. <c>assignedOnDate</c> считается на чтении, по назначениям <c>work-11</c>, и не хранится:
/// назначение могут поправить задним числом, и пометка обязана это увидеть (design <c>work-13</c>). Назначение любого
/// вида — монтажник или менеджер, — как у <c>/api/work/sites/mine</c>. Это та же пометка
/// <see cref="ReviewMarks.SiteNotAssigned" /> каталога (<c>work-16</c>), а не второй расчёт: поле и пометка не
/// расходятся.
/// </summary>
public sealed class ReportViews(WorkDbContext db, IModuleUser user, ReportReads reads)
{
    /// <summary>Отчёт, прочитанный с отслеживанием: версия — его текущая версия строки.</summary>
    public async Task<ReportDto> OneAsync(
        ReportViewer viewer, Report report, bool replayed = false, bool alreadySubmitted = false, CancellationToken ct = default) =>
        (await ManyAsync(viewer, [report], ct, replayed))[0] with { AlreadySubmitted = alreadySubmitted };

    /// <param name="versions">Версии отчётов, прочитанных без отслеживания (выдача курсора); не названы — версия
    /// отслеживаемой строки.</param>
    public async Task<IReadOnlyList<ReportDto>> ManyAsync(
        ReportViewer viewer, IReadOnlyList<Report> reports, CancellationToken ct, bool replayed = false,
        IReadOnlyDictionary<Guid, string>? versions = null)
    {
        var marks = await reads.MarksAsync(viewer, [.. reports.Select(r => r.Id)], user.Id, ct);
        return
        [
            .. reports.Select(r =>
            {
                var codes = marks.GetValueOrDefault(r.Id) ?? [];
                return Dto(r, !codes.Contains(ReviewMarks.SiteNotAssigned), replayed,
                    versions is null ? db.VersionOf(r) : versions[r.Id], AcceptWouldMark(r, user.Id), codes);
            }),
        ];
    }

    /// <summary>
    /// «Ввёл и принял один человек» (ТЗ WORK-31.1) — правило <see cref="ReviewMarks.AcceptWouldMarkRule" />: им же
    /// считается пометка <c>same-person</c> у поданного.
    /// </summary>
    public static bool AcceptWouldMark(Report r, Guid? viewer) => ReviewMarks.AcceptWouldMark(r, viewer);

    private static ReportDto Dto(
        Report r, bool assigned, bool replayed, string version, bool acceptWouldMark, IReadOnlyList<string> marks) => new(
        r.Id, r.EmployeeId, r.ConstructionId, r.Date, Report.ShiftCode(r.Shift), r.Hours, Report.StatusCode(r.Status),
        r.Note, r.Seq, version, assigned,
        r.EnteredByUserId is { } by ? new ReportEnteredByDto(by, r.EnteredByName ?? by.ToString()) : null,
        r.CreatedAt, r.UpdatedAt,
        [.. r.Lines.OrderBy(l => l.Position).Select(Line)],
        replayed,
        r.Status == ReportStatus.Draft ? r.WindowEndsAt : null,
        r.SubmittedAt,
        r.SubmittedBy is { } submitter ? Report.SubmitterCode(submitter) : null,
        r.RequiredMissing is { } missing ? Parsed(missing) : null,
        AlreadySubmitted: false, AcceptWouldMark: acceptWouldMark,
        Marks: [.. ReviewMarks.All.Where(m => marks.Contains(m.Code)).Select(m => m.Dto)]);

    private static JsonElement Parsed(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        return parsed.RootElement.Clone();
    }

    private static ReportLineDto Line(ReportLine l)
    {
        using var details = JsonDocument.Parse(l.Details);
        return new ReportLineDto(
            l.Id, l.Position, l.WorkTypeId, l.VariantId, l.Volume, l.UnitId, l.SectionId, l.Location, l.Comment,
            l.IsCrew, l.WorkPlanItemId, details.RootElement.Clone(),
            [.. l.Materials.OrderBy(m => m.Position).Select(m => new ReportMaterialDto(m.NomenclatureId, m.Note))],
            [.. l.Participants.Select(p => p.EmployeeId)]);
    }
}
