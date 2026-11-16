using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Access;
using BHS.CRG.Modules.Work.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BHS.CRG.Modules.Work.Review;

/// <summary>
/// Приёмка и возврат отчёта (ТЗ WORK-13, WORK-46, WORK-39, AUTH-8, AUTH-9; change <c>work-16-review</c>):
/// <c>POST /api/work/reports/{id}/accept</c> и <c>POST /api/work/reports/{id}/return</c>, оба с <c>If-Match</c>.
///
/// <para><b>Ворота — право <c>work.report.review</c></b>. Адреса стоят отдельно от группы <c>/api/work/reports</c>: та
/// закрыта правом своих отчётов, а политики группы и адреса складываются по «И» (как у журнала, <c>work-14</c>).
/// <c>work.report.read</c> действий не открывает — 403.</para>
///
/// <para><b>Стройка — охватом, а не правом</b>: стройки действующих назначений «менеджер», с <c>work.sites.all</c> — все.
/// Отчёт чужой стройки не находится — 404, как несуществующий: 403 подтверждал бы, что такой отчёт есть.</para>
/// </summary>
public static class ReportReviewEndpoints
{
    public const string AcceptPath = ReportEndpoints.Path + "/{id:guid}/accept";

    public const string ReturnPath = ReportEndpoints.Path + "/{id:guid}/return";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var review = AppPolicies.Permission(WorkModule.ReportReview);
        endpoints.MapPost(AcceptPath, AcceptAsync).WithTags("Отчёты").RequireAuthorization(review);
        endpoints.MapPost(ReturnPath, ReturnAsync).WithTags("Отчёты").RequireAuthorization(review);
    }

    private static Task<IResult> AcceptAsync(
        Guid id, AcceptRequest? body, HttpRequest request, IModuleReaders readers, ManagerSites sites, ReportReview review,
        CancellationToken ct) => ReportRefusals.RunAsync(async () =>
        await review.AcceptAsync(await EntryReportEndpoints.ViewerAsync(readers, sites, ct), id, body, request, ct));

    private static Task<IResult> ReturnAsync(
        Guid id, ReturnRequest? body, HttpRequest request, IModuleReaders readers, ManagerSites sites, ReportReview review,
        CancellationToken ct) => ReportRefusals.RunAsync(async () =>
        await review.ReturnAsync(await EntryReportEndpoints.ViewerAsync(readers, sites, ct), id, body, request, ct));
}
