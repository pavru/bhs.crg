using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Exchange;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BHS.CRG.Modules.Work.Endpoints;

internal static class MaterialsHint
{
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/work/materials/hint", GetAsync).WithTags("Отчёты")
            .RequireAuthorization(AppPolicies.AnyPermission(WorkModule.ReportOwn, WorkModule.ReportReview));

    private static async Task<MaterialHintResponse> GetAsync(Guid constructionId, Guid? workTypeId, Guid? unitId,
        Guid? sectionId, Guid? planItemId, DateOnly? date, string? q, MaterialHintAccess access,
        IModuleContracts contracts, MaterialHintEstimate estimate, MaterialHintChoices choices, CancellationToken ct)
    {
        var reader = await access.ForSiteAsync(constructionId, date, ct);
        var (issued, units) = await IssuedAsync(contracts, reader, constructionId, ct);
        var planned = await estimate.ReadAsync(reader, constructionId, workTypeId, unitId, sectionId, planItemId, ct);
        return await choices.BuildAsync(issued, planned, units, q, ct);
    }

    private static async Task<(MaterialHintSource Source, Dictionary<Guid, string?> Units)> IssuedAsync(
        IModuleContracts contracts, ModuleReader reader, Guid constructionId, CancellationToken ct)
    {
        var provider = contracts.Get<IMaterialsOnSite>();
        if (provider is ModuleContractAnswer<IMaterialsOnSite>.NotConnected missing)
            return (MaterialHintSource.NotConnected(missing.Text), []);

        var contract = ((ModuleContractAnswer<IMaterialsOnSite>.Connected)provider).Contract;
        return await contract.ReadHintAsync(reader, constructionId, ct) switch
        {
            ContractAnswer<IReadOnlyList<MaterialOnSiteHint>>.Shown shown =>
                (MaterialHintSource.Ok([.. shown.Value.Select(item => item.NomenclatureId)]),
                    shown.Value.ToDictionary(item => item.NomenclatureId, item => item.Unit)),
            ContractAnswer<IReadOnlyList<MaterialOnSiteHint>>.Hidden hidden =>
                (MaterialHintSource.Hidden(hidden.Text, hidden.Permission), []),
            _ => throw new ForbiddenException("Для подсказки материалов требуется доступ пользователя."),
        };
    }
}
