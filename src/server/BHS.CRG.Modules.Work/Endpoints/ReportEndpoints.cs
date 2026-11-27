using System.Globalization;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BHS.CRG.Modules.Work.Endpoints;

/// <summary>
/// Свои отчёты монтажника (ТЗ WORK-4, WORK-39, MOB-15, MOB-16, MOB-17, MOB-21, AUTH-8, AUTH-9; change
/// <c>work-13-report-model</c>): <c>GET /api/work/reports?since=&amp;limit=</c>, <c>POST</c>, и <c>GET</c>, <c>PUT</c>,
/// <c>DELETE /api/work/reports/{id}</c>, подача — <c>POST /api/work/reports/{id}/submit</c> (<c>work-15</c>).
///
/// <para><b>Ворота.</b> Группа модуля закрыта воротами <c>module:work</c>, эта группа — правом
/// <c>work.report.own</c>.</para>
///
/// <para><b>Сотрудник — из связи учётной записи</b> (<c>work-01</c>), не из тела: тело, назвавшее другого, — 400.
/// Учётная запись без связи — 403 с причиной, а не пустой список; справочника сотрудников нет — 409
/// <c>directory-missing</c>. Чужой отчёт отвечает тем же 404, что и несуществующий, — одним путём кода
/// (<see cref="ReportReads.FindAsync" />).</para>
/// </summary>
public static class ReportEndpoints
{
    public const string Path = "/api/work/reports";

    public const string NotLinked =
        "Учётная запись не связана с сотрудником: свои отчёты ведёт сотрудник, а связь ставит администратор в " +
        "карточке сотрудника.";

    /// <summary>Сколько записей отдаёт курсор за раз, если клиент не назвал.</summary>
    public const int DefaultLimit = 100;

    public const int MaxLimit = 500;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(Path).WithTags("Отчёты")
            .RequireAuthorization(AppPolicies.Permission(WorkModule.ReportOwn));
        group.MapGet("", ListAsync);
        group.MapPost("", CreateAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPut("/{id:guid}", ChangeAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);
        group.MapPost("/{id:guid}/submit", SubmitAsync);
    }

    /// <summary>Подать свой отчёт (ТЗ WORK-10, <c>work-15</c>): <c>If-Match</c>, тело — по желанию.</summary>
    private static Task<IResult> SubmitAsync(
        Guid id, SubmitRequest? body, HttpRequest request, IModuleUser user, IModuleEmployees employees, ReportDesk desk,
        CancellationToken ct) => ReportRefusals.RunAsync(async () =>
        await desk.SubmitAsync(ReportViewer.Own(await MeAsync(user, employees, ct)), id, body, request, ct));

    /// <summary>Свои отчёты, изменённые после <c>since</c>, удалённые с тех пор черновики и курсор (ТЗ MOB-16).</summary>
    private static Task<IResult> ListAsync(
        long? since, int? limit, IOwnReportReader reader, ReportReads reads, ReportViews views,
        CancellationToken ct) => ReportRefusals.RunAsync(async () =>
    {
        var after = since ?? 0;
        if (after < 0) throw new InvalidRequestException("Курсор (since) не может быть отрицательным.");
        var take = limit ?? DefaultLimit;
        if (take is < 1 or > MaxLimit)
            throw new InvalidRequestException($"Предел выдачи (limit) — от 1 до {MaxLimit}.");

        var viewer = await reader.ViewerAsync(ct);
        var feed = await reads.SinceAsync(viewer, after, take, ct);
        return Results.Ok(new
        {
            items = await views.ManyAsync(viewer, feed.Items, ct, versions: feed.Versions),
            deleted = feed.Deleted,
            cursor = feed.Cursor,
            more = feed.More,
        });
    });

    private static Task<IResult> CreateAsync(
        ReportRequest body, IModuleUser user, IModuleEmployees employees, ReportDesk desk, CancellationToken ct) =>
        ReportRefusals.RunAsync(async () =>
        {
            var me = await MeAsync(user, employees, ct);
            if (body.EmployeeId is { } named && named != me)
                throw new InvalidRequestException(
                    "Сотрудника отчёта (employeeId) задаёт связь учётной записи, а не тело запроса: свой отчёт — только " +
                    "свой. Отчёт за другого сотрудника вводит менеджер стройки.");
            return await desk.CreateAsync(Path, ReportViewer.Own(me), me, body, enteredBy: null, ct);
        });

    private static Task<IResult> GetAsync(
        Guid id, string? view, IOwnReportReader reader, ReportHistory history, CancellationToken ct) =>
        ReportRefusals.RunAsync(async () =>
        {
            var viewer = await reader.ViewerAsync(ct);
            return Results.Ok(await history.GetAsync(viewer, id, view, ct));
        });

    private static Task<IResult> ChangeAsync(
        Guid id, ReportRequest body, HttpRequest request, IModuleUser user, IModuleEmployees employees, ReportDesk desk,
        CancellationToken ct) => ReportRefusals.RunAsync(async () =>
        await desk.ChangeAsync(ReportViewer.Own(await MeAsync(user, employees, ct)), id, body, request, ct));

    /// <summary>
    /// Удалить свой черновик; <c>changeId</c> и <c>deviceTime</c> — в строке запроса (тела у DELETE нет). Время
    /// разбирается здесь, а не привязкой: незакодированный «+» смещения («+03:00») приходит в строке запроса пробелом,
    /// и привязка отвечала бы 400 без слов.
    /// </summary>
    private static Task<IResult> DeleteAsync(
        Guid id, Guid? changeId, string? deviceTime, HttpRequest request, IModuleUser user,
        IModuleEmployees employees, ReportDesk desk, CancellationToken ct) => ReportRefusals.RunAsync(async () =>
        await desk.RemoveDraftAsync(ReportViewer.Own(await MeAsync(user, employees, ct)), id, changeId,
            DeviceTimeOf(deviceTime), request, ct));

    /// <summary>Время устройства из строки запроса: ISO 8601; пробел на месте «+» смещения — тот же «+».</summary>
    internal static DateTimeOffset? DeviceTimeOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var iso = text.Trim().Replace(' ', '+');
        return DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at
            : throw new InvalidRequestException(
                $"Время устройства (deviceTime) «{text}» не разобрано: ожидается ISO 8601, например 2026-11-16T18:00:00+03:00.");
    }

    /// <summary>
    /// Сотрудник учётной записи. Справочника нет — 409 <c>directory-missing</c>; не связана — 403 с причиной. Различить
    /// их можно только вопросом о справочнике (как <c>/api/work/sites/mine</c>).
    /// </summary>
    internal static async Task<Guid> MeAsync(IModuleUser user, IModuleEmployees employees, CancellationToken ct)
    {
        var (me, reason) = await WorkAccounts.OfUserAsync(user, employees, ct);
        if (me is not null) return me.Id;
        if (reason == IModuleEmployees.NotSetUpCode) throw new WorkDirectoryMissingException(IModuleEmployees.NotSetUp);
        throw new ForbiddenException(NotLinked);
    }
}
