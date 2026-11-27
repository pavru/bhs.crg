using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Tables;
using BHS.CRG.Modules.Work.Access;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BHS.CRG.Modules.Work.Review;

/// <summary>Граница выдачи очереди (ТЗ CORE-24.3) — вид таблиц модуля (<c>work-07</c>): код охвата и строка.</summary>
/// <param name="Code"><c>sites</c> — стройки назначений «менеджер»; <c>all</c> — все стройки.</param>
/// <param name="Sites">Число строек охвата <c>sites</c>; у <c>all</c> — <c>null</c>.</param>
public sealed record ReviewBoundaryDto(string Code, string Text, int? Sites);

/// <summary>Сотрудник для отображения и отбора очереди. Охват берётся из поданных отчётов.</summary>
public sealed record ReviewQueueEmployeeDto(Guid Id, string Name);

/// <summary>Строка очереди приёмки.</summary>
/// <param name="Version">Значение для <c>If-Match</c> приёмки и возврата.</param>
/// <param name="Lines">Сколько строк в отчёте.</param>
/// <param name="Actions">Что читающий может сделать с отчётом: <c>accept</c>, <c>return</c>, <c>edit</c> (правка строк
/// адресом ввода); у читающего без <c>work.report.review</c> — пусто.</param>
/// <param name="AcceptWouldMark">Приёмка пометит отчёт «ввёл и принял один человек» (WORK-31.1).</param>
/// <param name="PhotosPending">Сколько фото объявлено строками и ещё не дошло (WORK-34, <c>work-17</c>).</param>
public sealed record ReviewQueueRowDto(
    Guid Id, Guid EmployeeId, Guid ConstructionId, DateOnly Date, string Shift, decimal? Hours, string Status,
    string Version, DateTimeOffset? SubmittedAt, string? SubmittedBy, ReportEnteredByDto? EnteredBy, int Lines,
    IReadOnlyList<ReviewMarkDto> Marks, IReadOnlyList<string> Actions, bool AcceptWouldMark, int PhotosPending);

/// <summary>
/// Очередь приёмки (ТЗ WORK-28, WORK-39, WORK-46, CORE-24.3, AUTH-8, AUTH-9; change <c>work-16-review</c>):
/// <c>GET /api/work/review/queue?constructionId=&amp;employeeId=&amp;from=&amp;to=&amp;mark=&amp;offset=&amp;limit=</c> —
/// поданные отчёты строек читающего. Черновики, возвращённые и принятые в очередь не входят.
///
/// <para><b>Ворота — «любое из прав»</b> <c>work.report.review</c> и <c>work.report.read</c>; монтажнику с одним
/// <c>work.report.own</c> — 403. Охват: <c>work.report.read</c> и <c>work.sites.all</c> — все стройки, иначе стройки
/// действующих назначений «менеджер». Действия (<c>actions</c>) — только по праву приёмки и в её охвате:
/// <c>work.report.read</c> открывает чтение, а не решение.</para>
///
/// <para><b>Пустая очередь называет причину</b> (<c>reason</c>): <c>not-assigned</c> — своих строек нет;
/// <c>not-linked</c> — учётная запись не связана с сотрудником, и назначений у неё быть не может; <c>directory-missing</c>
/// — справочника сотрудников нет;
/// <c>nothing-pending</c> — ждущих решения нет; <c>filtered-out</c> — ждущие есть, но отбор исключил все. Граница
/// (<c>boundary</c>) от отбора не зависит: «отчёты ваших строек (4)» и при отборе по одной стройке.</para>
/// </summary>
public static class ReviewQueue
{
    public const string Path = "/api/work/review/queue";

    public const int DefaultLimit = 50;

    public const int MaxLimit = 200;

    public const string NotAssigned = "not-assigned";

    public const string NotLinked = WorkAccounts.NotLinked;

    public const string NothingPending = "nothing-pending";

    public const string FilteredOut = "filtered-out";

    public const string Accept = "accept";

    public const string Return = "return";

    public const string Edit = "edit";

    internal static readonly IReadOnlyList<string> Decisions = [Accept, Return, Edit];

    /// <summary>Строки границы по исходу охвата — вид таблиц модуля (<see cref="ModuleTableBoundary" />, <c>work-07</c>).</summary>
    private static readonly IReadOnlyList<ModuleTableBoundary> Boundaries =
    [
        new("sites", $"отчёты ваших строек ({ModuleTableBoundary.SitesPlace})"),
        new(ModuleTableBoundary.OnlyCode, "все стройки"),
    ];

    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(Path, GetAsync).WithTags("Отчёты")
            .RequireAuthorization(AppPolicies.AnyPermission(WorkModule.ReportReview, WorkModule.ReportRead));

    private static Task<IResult> GetAsync(
        Guid? constructionId, Guid? employeeId, DateOnly? from, DateOnly? to, string? mark, int? offset, int? limit,
        IModuleUser user, IModuleReaders readers, ManagerSites sites, IModuleEmployees employees, ReportReads reads,
        IModuleAcceptanceFields acceptanceFields, IModuleWorkTypes workTypes, CancellationToken ct) =>
        ReportRefusals.RunAsync(async () =>
        {
            var skip = offset ?? 0;
            if (skip < 0) throw new InvalidRequestException("Сдвиг страницы (offset) не может быть отрицательным.");
            var take = limit ?? DefaultLimit;
            if (take is < 1 or > MaxLimit)
                throw new InvalidRequestException($"Строк на странице (limit) — от 1 до {MaxLimit}.");
            if (from is { } a && to is { } b && a > b)
                throw new InvalidRequestException("Начало периода (from) позже его конца (to).");
            ReviewMark? chosen = null;
            if (!string.IsNullOrWhiteSpace(mark))
                chosen = ReviewMarks.Of(mark.Trim()) ?? throw new InvalidRequestException(
                    $"Пометка «{mark}» неизвестна. Пометки: {string.Join(", ", ReviewMarks.All.Select(m => m.Code))}.");
            var filter = new ReviewQueueFilter(constructionId, employeeId, from, to, chosen);

            var reader = await readers.ForRequestAsync(ct);
            var decides = reader.Allows(WorkModule.ReportReview) ? await sites.ReachAsync(reader, ct) : null;
            var reach = reader.Allows(WorkModule.ReportRead) ? ModuleReach.All : decides!;
            var boundary = Boundary(reach);
            if (reach is { Kind: ModuleReachKind.Sites, Sites.Count: 0 })
            {
                // Своих строек нет: назначений нет — или их и быть не может, потому что нет связи или справочника.
                var (_, missing) = await WorkAccounts.OfUserAsync(user, employees, ct);
                return Results.Ok(Page([], 0, Empty(), [], boundary, missing ?? NotAssigned, skip, take));
            }

            var acceptance = await acceptanceFields.ReadAsync(WorkModule.ModuleCode, WorkRecordTypes.LineDetailsCode, ct);
            var read = await reads.QueueAsync(ReportViewer.Of(reach), filter, user.Id, skip, take, workTypes, ct, acceptance);
            string? reason = read.Total > 0 ? null : read.PendingEmployeeIds.Count > 0 && filter.Any ? FilteredOut : NothingPending;
            var rows = read.Items.Select(i => Row(i, user.Id, decides)).ToList();
            var ids = read.PendingEmployeeIds.Union(rows.Select(row => row.EmployeeId)).ToArray();
            var people = ids.Length == 0 ? [] : await employees.GetAsync(ids, ct) ?? [];
            var options = people.OrderBy(person => person.FullName, StringComparer.CurrentCulture).ThenBy(person => person.Id)
                .Select(person => new ReviewQueueEmployeeDto(person.Id, person.FullName)).ToList();
            return Results.Ok(Page(rows, read.Total, read.Counts, options, boundary, reason, skip, take));
        });

    private static object Page(
        IReadOnlyList<ReviewQueueRowDto> items, int total, IReadOnlyDictionary<string, int> counts,
        IReadOnlyList<ReviewQueueEmployeeDto> employees, ReviewBoundaryDto boundary, string? reason, int offset, int limit) =>
        new { items, total, offset, limit, counts, employees, boundary, reason };

    private static Dictionary<string, int> Empty() => ReviewMarks.All.ToDictionary(m => m.Code, _ => 0);

    private static ReviewBoundaryDto Boundary(ModuleReach reach)
    {
        var outcome = ModuleTableOutcome.Of(reach);
        return new(outcome.Code, Boundaries.Single(b => b.Code == outcome.Code).Render(outcome.Sites), outcome.Sites);
    }

    private static ReviewQueueRowDto Row(ReviewQueueItem item, Guid? viewer, ModuleReach? decides)
    {
        var r = item.Report;
        return new ReviewQueueRowDto(
            r.Id, r.EmployeeId, r.ConstructionId, r.Date, Report.ShiftCode(r.Shift), r.Hours, Report.StatusCode(r.Status),
            item.Version, r.SubmittedAt, r.SubmittedBy is { } by ? Report.SubmitterCode(by) : null,
            r.EnteredByUserId is { } author ? new ReportEnteredByDto(author, r.EnteredByName ?? author.ToString()) : null,
            item.Lines,
            ReviewMarks.Dtos(item.Marks, item.PhotosPending, item.MissingFields, item.Required),
            decides?.Covers(r.ConstructionId, own: false) == true ? Decisions : [],
            ReviewMarks.AcceptWouldMark(r, viewer), item.PhotosPending);
    }
}
