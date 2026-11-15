using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Access;
using BHS.CRG.Modules.Work.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BHS.CRG.Modules.Work.Endpoints;

/// <summary>
/// Журнал отчёта (ТЗ WORK-11, WORK-12, WORK-39, WORK-54, AUTH-8, AUTH-9; change <c>work-14-report-journal</c>):
/// <c>GET /api/work/reports/{id}/journal</c>. Пишущих адресов у журнала нет и не будет — <c>POST</c>, <c>PUT</c> и
/// <c>DELETE</c> отвечают 405: журнал пишет только путь сохранения отчёта.
///
/// <para><b>Ворота — «любое из прав»</b> <c>work.report.own</c>, <c>work.report.review</c>, <c>work.report.read</c>.
/// Адрес стоит отдельно от группы <c>/api/work/reports</c>: та закрыта правом своих отчётов, а политики группы и адреса
/// складываются по «И» — руководитель с одним правом чтения получил бы 403.</para>
///
/// <para><b>Охват — по правам, и права складываются</b>: <c>work.report.read</c> — все отчёты; <c>work.report.review</c>
/// — стройки менеджера (с <c>work.sites.all</c> — все); <c>work.report.own</c> — свои. Черновик виден только сотруднику
/// отчёта и автору его ввода. Чужой, черновик чужого и несуществующий отчёт отвечают одним 404 одним путём кода
/// (<see cref="ReportReads.JournalAsync" />).</para>
/// </summary>
public static class ReportJournalEndpoint
{
    public const string Path = ReportEndpoints.Path + "/{id:guid}/journal";

    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(Path, GetAsync).WithTags("Отчёты")
            .RequireAuthorization(AppPolicies.AnyPermission(WorkModule.ReportOwn, WorkModule.ReportReview, WorkModule.ReportRead));

    private static Task<IResult> GetAsync(
        Guid id, IModuleUser user, IModuleReaders readers, ManagerSites sites, IModuleEmployees employees,
        ReportReads reads, CancellationToken ct) => ReportRefusals.RunAsync(async () =>
    {
        var entries = await reads.JournalAsync(await ViewerAsync(user, readers, sites, employees, ct), user.Id, id, ct)
                      ?? throw new NotFoundException(ReportDesk.NotFoundText);
        return Results.Ok(new { items = entries.Select(ReportJournalView.Of) });
    });

    /// <summary>
    /// Охват читателя журнала. Только свои отчёты — сотрудник обязателен: без связи 403 с причиной, как у своих
    /// отчётов. С правами шире — сотрудник по связи, если она есть: его отчёты видны и вне охвата строек.
    /// </summary>
    private static async Task<ReportViewer> ViewerAsync(
        IModuleUser user, IModuleReaders readers, ManagerSites sites, IModuleEmployees employees, CancellationToken ct)
    {
        var reader = await readers.ForRequestAsync(ct);
        var wider = reader.Allows(WorkModule.ReportRead) || reader.Allows(WorkModule.ReportReview);
        if (!wider) return ReportViewer.Own(await ReportEndpoints.MeAsync(user, employees, ct));

        // Свой сотрудник нужен и при охвате «все»: свой черновик виден только сотруднику отчёта.
        var me = reader.Allows(WorkModule.ReportOwn) && user.Id is { } account
            ? (await employees.OfAccountAsync(account, ct))?.Id
            : null;
        return reader.Allows(WorkModule.ReportRead)
            ? ReportViewer.Of(ModuleReach.All, me)
            : ReportViewer.Of(await sites.ReachAsync(reader, ct), me);
    }
}
