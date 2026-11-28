using System.Text.Json;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Closing;
using BHS.CRG.Modules.Work.Data;

namespace BHS.CRG.Modules.Work.Endpoints;

public sealed record ReportMaterialDto(Guid NomenclatureId, string? Note);

/// <summary>
/// Фото строки в ответе отчёта (ТЗ WORK-34, <c>work-17</c>): <c>state</c> — <c>pending</c> (объявлено, файла нет) |
/// <c>stored</c>. Файл — адресами <c>/api/work/photos/{id}</c> и <c>…/preview</c>.
/// </summary>
public sealed record ReportPhotoDto(Guid Id, string State)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? OriginalName { get; init; }
    // Только ответ с действующим состоянием периода даёт разрешение на досылку.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? CanDeliver { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? VersionUrl { get; init; }
}

public sealed record ReportLineDto(
    Guid Id, int Position, Guid WorkTypeId, Guid? VariantId, decimal Volume, Guid UnitId, Guid? SectionId,
    string? Location, string? Comment, bool IsCrew, Guid WorkPlanItemId, JsonElement Details,
    IReadOnlyList<ReportMaterialDto> Materials, IReadOnlyList<Guid> Participants, IReadOnlyList<ReportPhotoDto> Photos,
    string? CrewRole)
{
    // Снимок версии хранит значения, а пометки рассчитываются при чтении по действующей схеме.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ModuleAcceptanceField>? MissingFields { get; init; }
}

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
/// <param name="PhotosPending">Сколько фото строк объявлено и ещё не дошло (WORK-34, <c>work-17</c>): отчёт их не ждёт,
/// подать и принять его можно.</param>
public sealed record ReportDto(
    Guid Id, Guid EmployeeId, Guid ConstructionId, DateOnly Date, string Shift, decimal? Hours, string Status,
    string? Note, long Seq, string Version, bool AssignedOnDate, ReportEnteredByDto? EnteredBy,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<ReportLineDto> Lines, bool Replayed,
    DateTimeOffset? AutoSubmitAt, DateTimeOffset? SubmittedAt, string? SubmittedBy, JsonElement? RequiredMissing,
    bool AlreadySubmitted, bool AcceptWouldMark, IReadOnlyList<ReviewMarkDto> Marks, int PhotosPending,
    int CurrentVersion, IReadOnlyList<ReportVersionDto> Versions, string View)
{
    public string? ConstructionName { get; init; }
    public string? TimeZoneId { get; init; }
    public DateOnly? Today { get; init; }
    public string? EmployeeName { get; init; }
    public string? ReturnComment { get; init; }
    public bool PeriodClosed { get; init; }
    public int MissingFieldsCount { get; init; }
    internal bool HasWebFields { get; init; }
}

/// <summary>
/// Отчёты в ответах адресов. <c>assignedOnDate</c> считается на чтении, по назначениям <c>work-11</c>, и не хранится:
/// назначение могут поправить задним числом, и пометка обязана это увидеть (design <c>work-13</c>). Назначение любого
/// вида — монтажник или менеджер, — как у <c>/api/work/sites/mine</c>. Это та же пометка
/// <see cref="ReviewMarks.SiteNotAssigned" /> каталога (<c>work-16</c>), а не второй расчёт: поле и пометка не
/// расходятся.
/// </summary>
public sealed class ReportViews(
    WorkDbContext db, IModuleUser user, ReportReads reads, IModuleConstructions constructions,
    IModuleEmployees employees, IModuleClock clock, ReportPeriods periods, IModuleAcceptanceFields acceptanceFields)
{
    private Task<ModuleAcceptanceSchema>? _acceptance;

    private Task<ModuleAcceptanceSchema> ReadAcceptanceAsync(CancellationToken ct) =>
        _acceptance ??= acceptanceFields.ReadAsync(WorkModule.ModuleCode, WorkRecordTypes.LineDetailsCode, ct);

    /// <summary>Отчёт, прочитанный с отслеживанием: версия — его текущая версия строки.</summary>
    public async Task<ReportDto> OneAsync(
        ReportViewer viewer, Report report, bool replayed = false, bool alreadySubmitted = false, CancellationToken ct = default)
    {
        var dto = (await ManyAsync(viewer, [report], ct, replayed))[0] with { AlreadySubmitted = alreadySubmitted };
        if (dto.PhotosPending == 0) return dto;

        var closing = dto.PeriodClosed ? await periods.ClosingAsync(report, ct) : null;
        return await WithAcceptanceAsync(dto with
        {
            Lines = LinesOf(report, p => ReportPeriods.DeliveryAllowed(p, dto.PeriodClosed, closing)),
        }, ct);
    }

    /// <param name="versions">Версии отчётов, прочитанных без отслеживания (выдача курсора); не названы — версия
    /// отслеживаемой строки.</param>
    public async Task<IReadOnlyList<ReportDto>> ManyAsync(
        ReportViewer viewer, IReadOnlyList<Report> reports, CancellationToken ct, bool replayed = false,
        IReadOnlyDictionary<Guid, string>? versions = null)
    {
        if (reports.Count == 0) return [];
        var acceptance = await ReadAcceptanceAsync(ct);
        var hasWebFields = viewer.FieldDelivery is not null && reports.Any(r => r.Lines.Count > 0)
            && await acceptanceFields.FormAsync(WorkModule.ModuleCode, WorkRecordTypes.LineDetailsCode, ct) is { } form
            && form.GetProperty("fields").GetArrayLength() > 0;
        Guid[] ids = [.. reports.Select(r => r.Id)];
        var marks = await reads.MarksAsync(viewer, ids, user.Id, ct);
        var delivery = viewer.FieldDelivery is null ? await reads.DeliveryMarksAsync(viewer, ids, ct)
            : new Dictionary<Guid, IReadOnlyList<ReviewMarkDto>>();
        var history = await reads.VersionListsAsync(viewer, user.Id, reports, ct);
        var sites = (await constructions.ListAsync(ct)).ToDictionary(site => site.Id);
        var today = new Dictionary<Guid, DateOnly?>();
        foreach (var id in reports.Select(r => r.ConstructionId).Distinct())
            today[id] = await clock.TodayOnSiteAsync(id, ct);
        var people = (await employees.GetAsync([.. reports.Select(r => r.EmployeeId).Distinct()], ct) ?? [])
            .ToDictionary(person => person.Id, person => person.FullName);
        var boundaries = await periods.CurrentAsync(ct);
        var returns = await reads.ReturnCommentsAsync(viewer,
            [.. reports.Where(r => r.Status == ReportStatus.Returned).Select(r => r.Id)], ct);
        return
        [
            .. reports.Select(r =>
            {
                var codes = marks.GetValueOrDefault(r.Id) ?? [];
                var dto = ApplyAcceptance(Dto(r, !codes.Contains(ReviewMarks.SiteNotAssigned), replayed,
                    versions is null ? db.VersionOf(r) : versions[r.Id], AcceptWouldMark(r, user.Id), codes,
                    history.GetValueOrDefault(r.Id) ?? []) with
                {
                    ConstructionName = sites.GetValueOrDefault(r.ConstructionId)?.Name,
                    TimeZoneId = sites.GetValueOrDefault(r.ConstructionId)?.TimeZoneId,
                    Today = today[r.ConstructionId],
                    EmployeeName = people.GetValueOrDefault(r.EmployeeId),
                    PeriodClosed = ReportPeriods.IsClosed(boundaries, r.ConstructionId, r.Date),
                    ReturnComment = returns.GetValueOrDefault(r.Id),
                    HasWebFields = hasWebFields && r.Lines.Count > 0,
                }, acceptance);
                return dto with { Marks = ReviewMarks.WithText(dto.Marks, delivery.GetValueOrDefault(r.Id)) };
            }),
        ];
    }

    internal async Task<ReportDto> WithAcceptanceAsync(ReportDto dto, CancellationToken ct) =>
        ApplyAcceptance(dto, await ReadAcceptanceAsync(ct));

    private static ReportDto ApplyAcceptance(ReportDto dto, ModuleAcceptanceSchema acceptance)
    {
        var lines = dto.Lines.Select(line => line with { MissingFields = acceptance.Missing(line.Details) }).ToArray();
        var count = lines.Sum(line => line.MissingFields!.Count);
        return dto with
        {
            Lines = lines,
            MissingFieldsCount = count,
            Marks = ReviewMarks.WithText(
                ReviewMarks.Dtos(dto.Marks.Select(mark => mark.Code).ToArray(), dto.PhotosPending, count),
                dto.Marks.Where(mark => mark.Code != ReviewMarks.FieldsMissing).ToList()),
        };
    }

    /// <summary>
    /// «Ввёл и принял один человек» (ТЗ WORK-31.1) — правило <see cref="ReviewMarks.AcceptWouldMarkRule" />: им же
    /// считается пометка <c>same-person</c> у поданного.
    /// </summary>
    public static bool AcceptWouldMark(Report r, Guid? viewer) => ReviewMarks.AcceptWouldMark(r, viewer);

    private static ReportDto Dto(
        Report r, bool assigned, bool replayed, string version, bool acceptWouldMark, IReadOnlyList<string> marks,
        IReadOnlyList<ReportVersionDto> history)
    {
        var pending = PhotosPendingOf(r);
        return new ReportDto(
            r.Id, r.EmployeeId, r.ConstructionId, r.Date, Report.ShiftCode(r.Shift), r.Hours,
            Report.StatusCode(r.Status), r.Note, r.Seq, version, assigned,
            r.EnteredByUserId is { } by ? new ReportEnteredByDto(by, r.EnteredByName ?? by.ToString()) : null,
            r.CreatedAt, r.UpdatedAt,
            LinesOf(r),
            replayed,
            r.Status == ReportStatus.Draft ? r.WindowEndsAt : null,
            r.SubmittedAt,
            r.SubmittedBy is { } submitter ? Report.SubmitterCode(submitter) : null,
            r.RequiredMissing is { } missing ? Parsed(missing) : null,
            AlreadySubmitted: false, AcceptWouldMark: acceptWouldMark,
            Marks: ReviewMarks.Dtos(marks, pending), PhotosPending: pending,
            r.CurrentVersion, history, ReportHistory.Corrected);
    }

    /// <summary>Недогруженные фото отчёта — строки обязаны быть загружены вместе с фото.</summary>
    public static int PhotosPendingOf(Report r) =>
        r.Lines.Sum(l => l.Photos.Count(p => p.State == LinePhotoState.Pending));

    internal static IReadOnlyList<ReportLineDto> LinesOf(Report report, Func<LinePhoto, bool>? canDeliver = null) =>
        [.. report.Lines.OrderBy(l => l.Position).Select(l => Line(l, canDeliver))];

    private static JsonElement Parsed(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        return parsed.RootElement.Clone();
    }

    private static ReportLineDto Line(ReportLine l, Func<LinePhoto, bool>? canDeliver)
    {
        using var details = JsonDocument.Parse(l.Details);
        return new ReportLineDto(
            l.Id, l.Position, l.WorkTypeId, l.VariantId, l.Volume, l.UnitId, l.SectionId, l.Location, l.Comment,
            l.IsCrew, l.WorkPlanItemId, details.RootElement.Clone(),
            [.. l.Materials.OrderBy(m => m.Position).Select(m => new ReportMaterialDto(m.NomenclatureId, m.Note))],
            [.. l.Participants.Select(p => p.EmployeeId)],
            [.. l.Photos.OrderBy(p => p.DeclaredAt).ThenBy(p => p.Id).Select(p => new ReportPhotoDto(p.Id, LinePhoto.StateCode(p.State))
            {
                OriginalName = p.OriginalName,
                CanDeliver = p.State == LinePhotoState.Pending ? canDeliver?.Invoke(p) : null,
            })],
            l.CrewRole);
    }
}
