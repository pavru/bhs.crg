using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Access;
using BHS.CRG.Modules.Work.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BHS.CRG.Modules.Work.Endpoints;

/// <summary>
/// Ввод отчёта за сотрудника (ТЗ WORK-31, WORK-2, AUTH-8, AUTH-9; change <c>work-13-report-model</c>): <c>POST
/// /api/work/entry/reports</c>, <c>GET</c> и <c>PUT /api/work/entry/reports/{id}</c> — для сотрудника без смартфона.
///
/// <para><b>Отдельная группа, а не флаг у своих отчётов</b>: право и охват у них разные — здесь
/// <c>work.report.review</c> и стройки менеджера (<see cref="ManagerSites.ReachAsync" />; с <c>work.sites.all</c> —
/// все). Один адрес с двумя ветками прав спрятал бы ветку «чужой сотрудник» от переписи ворот.</para>
///
/// <para><b>Сотрудник — из тела, автор ввода — из учётной записи</b>: отчёт принадлежит сотруднику и приходит в его
/// курсор <c>since</c>, а <c>enteredBy</c> называет менеджера. Правила ключа, строк, повтора и версии — те же, что у
/// своих отчётов (<see cref="ReportDesk" />).</para>
///
/// <para><b>Стройка вне своих — 404</b>, и на заведении тоже: отчёт чужой стройки неотличим от несуществующего, а
/// заведение называет стройку «не найдена среди ваших строек».</para>
/// </summary>
public static class EntryReportEndpoints
{
    public const string Path = "/api/work/entry/reports";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(Path).WithTags("Отчёты")
            .RequireAuthorization(AppPolicies.Permission(WorkModule.ReportReview));
        group.MapPost("", CreateAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPut("/{id:guid}", ChangeAsync);
    }

    private static Task<IResult> CreateAsync(
        ReportRequest body, IModuleUser user, IModuleReaders readers, ManagerSites sites, IModuleEmployees employees,
        ReportDesk desk, CancellationToken ct) => ReportRefusals.RunAsync(async () =>
    {
        var employeeId = body.EmployeeId ?? throw new InvalidRequestException(
            "Не назван сотрудник отчёта (employeeId): за кого вводится отчёт.");
        var site = body.ConstructionId ?? throw new InvalidRequestException(ReportRules.NoSite);
        var viewer = await ViewerAsync(readers, sites, ct);
        if (!viewer.Covers(site, employeeId)) throw new NotFoundException(ReportDesk.SiteNotYours);

        var found = await employees.GetAsync([employeeId], ct)
                    ?? throw new WorkDirectoryMissingException(IModuleEmployees.NotSetUp);
        if (found.Count == 0) throw new InvalidRequestException("Сотрудник отчёта (employeeId) не найден.");

        return await desk.CreateAsync(Path, viewer, employeeId, body, (user.Id!.Value, user.Name), ct);
    });

    private static Task<IResult> GetAsync(
        Guid id, IModuleReaders readers, ManagerSites sites, ReportReads reads, ReportViews views, CancellationToken ct) =>
        ReportRefusals.RunAsync(async () =>
        {
            var report = await reads.FindAsync(await ViewerAsync(readers, sites, ct), id, ct)
                         ?? throw new NotFoundException(ReportDesk.NotFoundText);
            return Results.Ok(await views.OneAsync(report, ct: ct));
        });

    private static Task<IResult> ChangeAsync(
        Guid id, ReportRequest body, HttpRequest request, IModuleReaders readers, ManagerSites sites, ReportDesk desk,
        CancellationToken ct) => ReportRefusals.RunAsync(async () =>
        await desk.ChangeAsync(await ViewerAsync(readers, sites, ct), id, body, request, ct));

    /// <summary>Охват менеджера: стройки его действующих назначений «менеджер» или все — с <c>work.sites.all</c>.</summary>
    private static async Task<ReportViewer> ViewerAsync(IModuleReaders readers, ManagerSites sites, CancellationToken ct) =>
        ReportViewer.Of(await sites.ReachAsync(await readers.ForRequestAsync(ct), ct));
}
