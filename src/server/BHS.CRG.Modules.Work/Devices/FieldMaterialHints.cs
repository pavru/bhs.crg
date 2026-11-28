using BHS.CRG.Domain.Schema;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Work.Devices;

internal sealed class FieldMaterialHints(IModuleContracts contracts, IModuleCatalog catalog, FieldReferencePages pages)
{
    private sealed record Issued(Guid Id, string? DisplayName, string? Unit);
    private sealed record Estimated(Guid Id, Guid PlanItemId, string? DisplayName);

    internal async Task<object> ReadAsync(string kind, AssignedFieldSite? site, string scope, string? cursor,
        int limit, CancellationToken ct)
    {
        if (site is null) return pages.Page<object>(scope, cursor, limit, "not-assigned", []);
        return kind == "issued-materials"
            ? await IssuedAsync(site, scope, cursor, limit, ct)
            : await EstimatedAsync(site, scope, cursor, limit, ct);
    }

    private async Task<object> IssuedAsync(AssignedFieldSite site, string scope, string? cursor,
        int limit, CancellationToken ct)
    {
        if (contracts.Get<IMaterialsOnSite>() is not ModuleContractAnswer<IMaterialsOnSite>.Connected source)
            return pages.Page<Issued>(scope, cursor, limit, "not-connected", []);
        var hints = await source.Contract.ReadFieldHintsAsync(site, ct);
        var names = await NamesAsync(hints.Select(h => h.NomenclatureId), ct);
        if (names is null) return pages.Page<Issued>(scope, cursor, limit, "not-configured", []);
        var items = hints.Where(h => names.ContainsKey(h.NomenclatureId)).OrderBy(h => h.NomenclatureId)
            .Select(h => new Issued(h.NomenclatureId, names[h.NomenclatureId], h.Unit)).ToArray();
        return pages.Page(scope, cursor, limit, "ok", items);
    }

    private async Task<object> EstimatedAsync(AssignedFieldSite site, string scope, string? cursor,
        int limit, CancellationToken ct)
    {
        if (contracts.Get<IPlannedWorks>() is not ModuleContractAnswer<IPlannedWorks>.Connected source)
            return pages.Page<Estimated>(scope, cursor, limit, "not-connected", []);
        var hints = await source.Contract.ReadFieldHintsAsync(site, ct);
        var names = await NamesAsync(hints.Select(h => h.NomenclatureId), ct);
        if (names is null) return pages.Page<Estimated>(scope, cursor, limit, "not-configured", []);
        var items = hints.Where(h => names.ContainsKey(h.NomenclatureId))
            .OrderBy(h => h.PlanItemId).ThenBy(h => h.NomenclatureId)
            .Select(h => new Estimated(h.NomenclatureId, h.PlanItemId, names[h.NomenclatureId])).ToArray();
        return pages.Page(scope, cursor, limit, "ok", items);
    }

    private async Task<Dictionary<Guid, string?>?> NamesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToArray();
        if (wanted.Length == 0) return [];
        var refs = await catalog.RefsAsync(FunctionalTag.RoleNomenclature, wanted, ct);
        return refs?.Where(r => !r.Archived).ToDictionary(r => r.Id, r => r.DisplayName);
    }
}
