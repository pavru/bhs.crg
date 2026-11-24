using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BHS.CRG.Modules.Work.Endpoints;

public static class ReportDetailsSchema
{
    public const string Path = "/api/work/line-details-schema";

    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(Path, GetAsync).WithTags("Отчёты")
            .RequireAuthorization(AppPolicies.AnyPermission(WorkModule.ReportOwn, WorkModule.ReportReview));

    private static Task<IResult> GetAsync(IModuleAcceptanceFields fields, CancellationToken ct) =>
        ReportRefusals.RunAsync(async () =>
            Results.Ok(await fields.FormAsync(WorkModule.ModuleCode, WorkRecordTypes.LineDetailsCode, ct)
                ?? throw new ConflictException("Схема сведений строки не заведена. Проверьте журнал запуска.")));
}
