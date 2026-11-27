using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Costs.Endpoints;

internal static class EstimateVsCostsEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/costs/estimate-vs-costs")
            .RequireAuthorization(AppPolicies.Permission(CostsModule.ReportRead))
            .WithTags("План против факта материалов");
        group.MapGet("/", async (Guid site, Guid? section, string? search, bool? onlyExcess,
            EstimateVsCostsReport report, CancellationToken ct) =>
                Results.Ok(await report.ReadAsync(site, section, ct, search, onlyExcess == true)));
        group.MapGet("/constructions", async (IModuleConstructions constructions, CancellationToken ct) =>
            Results.Ok(await constructions.ListAsync(ct)));
        group.MapGet("/export", async (Guid site, Guid? section, string? search, bool? onlyExcess,
            EstimateVsCostsReport report, IModuleSpreadsheets spreadsheets, CancellationToken ct) =>
        {
            var view = await report.ReadAsync(site, section, ct, search, onlyExcess == true);
            return Results.File(spreadsheets.Xlsx(EstimateVsCostsExport.Sheet(view)),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "План против факта.xlsx");
        });
    }
}
