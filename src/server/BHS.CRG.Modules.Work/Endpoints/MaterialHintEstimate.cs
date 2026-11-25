using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Work.Endpoints;

internal sealed record MaterialHintSource(string State, IReadOnlyList<Guid> Ids, string? Reason = null, string? Permission = null)
{
    public static MaterialHintSource Ok(IReadOnlyList<Guid> ids) => new("ok", ids);
    public static MaterialHintSource NotConnected(string reason) => new("not-connected", [], reason);
    public static MaterialHintSource Hidden(string reason, string permission) => new("hidden", [], reason, permission);
}

internal sealed class MaterialHintEstimate(IModuleContracts contracts, IModulePlanItems positions)
{
    private static readonly MaterialHintSource NoItem = new("no-item", [], "У этой работы нет позиции сметы.");

    public async Task<MaterialHintSource> ReadAsync(ModuleReader reader, Guid constructionId,
        Guid? workTypeId, Guid? unitId, Guid? sectionId, Guid? planItemId, CancellationToken ct)
    {
        var provider = contracts.Get<IPlannedWorks>();
        if (provider is ModuleContractAnswer<IPlannedWorks>.NotConnected missing)
            return MaterialHintSource.NotConnected(missing.Text);
        if (PlannedHint.Refusal(reader) is ContractAnswer<IReadOnlyList<PlannedHint>>.Hidden hiddenByPermission)
            return MaterialHintSource.Hidden(hiddenByPermission.Text, hiddenByPermission.Permission);

        var key = workTypeId is { } work && unitId is { } unit
            ? new PlanItemKey(work, constructionId, sectionId, unit)
            : (PlanItemKey?)null;
        var stored = planItemId is { } id ? (await positions.GetAsync([id], ct)).SingleOrDefault() : null;
        var matching = stored is not null && stored.Key.ConstructionId == constructionId
            && (workTypeId is null || stored.Key.WorkTypeId == workTypeId) && (key is null || stored.Key == key);
        if (!matching && key is null) return NoItem;

        var contract = ((ModuleContractAnswer<IPlannedWorks>.Connected)provider).Contract;
        var query = new PlannedWorksQuery(constructionId, PlanItemIds: matching ? [stored!.Id] : null);
        var answer = await contract.ReadAsync(reader, query, ct);
        if (answer is not ContractAnswer<PlannedWorksAnswer>.Shown shown)
            throw new ForbiddenException("Для подсказки материалов требуется доступ пользователя.");
        if (shown.Value.Hint is ContractAnswer<IReadOnlyList<PlannedHint>>.Hidden hidden)
            return MaterialHintSource.Hidden(hidden.Text, hidden.Permission);

        var hints = ((ContractAnswer<IReadOnlyList<PlannedHint>>.Shown)shown.Value.Hint).Value;
        var position = matching ? stored!.Id : await FindByKeyAsync(hints, key!.Value, ct);
        return hints.SingleOrDefault(hint => hint.PlanItemId is not null && hint.PlanItemId == position) is { } hint
            ? MaterialHintSource.Ok(hint.NomenclatureIds)
            : NoItem;
    }

    private async Task<Guid?> FindByKeyAsync(IReadOnlyList<PlannedHint> hints, PlanItemKey key, CancellationToken ct)
    {
        // Plan-23 отдаёт все позиции действующих редакций стройки. Порт читает существующие ключи по ID;
        // поиск по точному ключу не заводит позицию при открытии подсказки.
        var ids = hints.Where(hint => hint.PlanItemId.HasValue).Select(hint => hint.PlanItemId!.Value).ToArray();
        return (await positions.GetAsync(ids, ct)).SingleOrDefault(item => item.Key == key)?.Id;
    }
}
