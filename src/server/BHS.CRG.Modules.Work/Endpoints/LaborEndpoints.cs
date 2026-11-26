using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Labor;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BHS.CRG.Modules.Work.Endpoints;

internal static class LaborEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/work/labor").WithTags("Трудозатраты")
            .RequireAuthorization(AppPolicies.AnyPermission(WorkModule.ReportReview, WorkModule.ReportRead));
        group.MapGet("/estimate-vs-actual", EstimateAsync);
        group.MapGet("/productivity", ProductivityAsync);
    }

    private static async Task<IResult> EstimateAsync(Guid constructionId, Guid? sectionId, DateOnly from, DateOnly to,
        IModuleReaders readers, LaborAnalytics analytics, CancellationToken ct) => Results.Ok(
        await analytics.CompareAsync(await readers.ForRequestAsync(ct), constructionId, sectionId, from, to, ct));

    private static async Task<IResult> ProductivityAsync(Guid? constructionId, Guid? sectionId, DateOnly from, DateOnly to,
        Guid? workTypeId, Guid? unitId, bool? byEmployee, IModuleReaders readers, LaborAnalytics analytics,
        CancellationToken ct)
    {
        if (workTypeId.HasValue != unitId.HasValue || workTypeId == Guid.Empty || unitId == Guid.Empty)
            throw new InvalidRequestException("Для выработки укажите вид работы (workTypeId) и единицу (unitId) вместе.");
        var query = workTypeId is { } type && unitId is { } unit ? new WorkProductivityQuery(type, unit) : null;
        return Results.Ok(await analytics.ReadProductivityAsync(await readers.ForRequestAsync(ct), constructionId,
            sectionId, from, to, query, byEmployee == true, ct));
    }
}
