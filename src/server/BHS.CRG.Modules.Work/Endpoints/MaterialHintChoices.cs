using System.Text.Json.Serialization;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Work.Endpoints;

internal sealed record MaterialHintItem(Guid Id, string? DisplayName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FullName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MatchedAlias,
    string? Unit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? NotIssued = null);

internal sealed record MaterialHintTier(string State, IReadOnlyList<MaterialHintItem> Items,
    string? Reason = null, string? Permission = null, int? InArchive = null, bool? More = null);

internal sealed record MaterialHintResponse(MaterialHintTier Issued, MaterialHintTier Estimate, MaterialHintTier Catalog);

internal sealed class MaterialHintChoices(IModuleCatalog catalog)
{
    private const int Limit = 100;
    private const string Role = FunctionalTag.RoleNomenclature;
    private const string MissingCatalog = "Справочник номенклатуры не заведён.";

    public async Task<MaterialHintResponse> BuildAsync(MaterialHintSource issued, MaterialHintSource estimate,
        IReadOnlyDictionary<Guid, string?> issuedUnits, string? q, CancellationToken ct)
    {
        var issuedIds = issued.Ids.ToHashSet();
        var estimateIds = estimate.Ids.Where(id => !issuedIds.Contains(id)).ToHashSet();
        var above = issuedIds.Concat(estimateIds).ToArray();
        var query = q?.Replace('\r', ' ').Replace('\n', ' ').Trim();
        // При поиске с верхними ярусами нужен полный набор совпавших ссылок: совпадение по альтернативному
        // имени может оказаться после лимита справочника, но принадлежать первому ярусу.
        var limit = string.IsNullOrEmpty(query) || above.Length == 0 ? Limit + 1 + above.Length : 0;
        var search = await catalog.SearchAsync(Role, query, limit, ct);
        IReadOnlyList<ModuleCatalogRef> upper = search is null || above.Length == 0 ? []
            : string.IsNullOrEmpty(query) ? await catalog.RefsAsync(Role, above, ct) ?? [] : search.Items;

        var issuedTier = Tier(issued, upper, issuedIds, search is not null,
            item => Item(item) with { Unit = issuedUnits.GetValueOrDefault(item.Id) });
        var estimateTier = Tier(estimate, upper, estimateIds, search is not null,
            item => Item(item) with { NotIssued = issued.State == "ok" ? true : null });

        if (search is null)
            return new(issuedTier, estimateTier, new("not-connected", [], MissingCatalog));

        var fromCatalog = search.Items.Where(item => !issuedIds.Contains(item.Id) && !estimateIds.Contains(item.Id))
            .Take(Limit + 1).ToArray();
        return new(issuedTier, estimateTier,
            new("ok", [.. fromCatalog.Take(Limit).Select(Item)], InArchive: search.InArchive, More: fromCatalog.Length > Limit));
    }

    private static MaterialHintTier Tier(MaterialHintSource source, IReadOnlyList<ModuleCatalogRef> upper,
        HashSet<Guid> ids, bool hasCatalog, Func<ModuleCatalogRef, MaterialHintItem> map)
    {
        if (source.State != "ok") return new(source.State, [], source.Reason, source.Permission);
        if (ids.Count > 0 && !hasCatalog) return new("not-connected", [], MissingCatalog);

        return new("ok", [.. upper.Where(item => !item.Archived && ids.Contains(item.Id))
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Id).Select(map)]);
    }

    private static MaterialHintItem Item(ModuleCatalogRef item) => new(item.Id, item.DisplayName, item.FullName, item.MatchedAlias);
}
