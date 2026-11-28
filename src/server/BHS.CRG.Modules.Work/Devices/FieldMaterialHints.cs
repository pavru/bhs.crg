using BHS.CRG.Domain.Schema;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Work.Devices;

internal sealed class FieldMaterialHints(IModuleContracts contracts, IModuleCatalog catalog)
{
    private sealed record Issued(Guid Id, string? DisplayName, string? Unit);
    private sealed record Estimated(Guid Id, Guid PlanItemId, string? DisplayName);

    internal Task<string> ReadRevisionAsync(string kind, CancellationToken ct) => kind switch
    {
        "issued-materials" when contracts.Get<IMaterialsOnSite>() is ModuleContractAnswer<IMaterialsOnSite>.Connected issued =>
            issued.Contract.ReadFieldRevisionAsync(ct),
        "estimated-materials" when contracts.Get<IPlannedWorks>() is ModuleContractAnswer<IPlannedWorks>.Connected estimate =>
            estimate.Contract.ReadFieldRevisionAsync(ct),
        _ => Task.FromResult("not-connected"),
    };

    internal async Task<FieldReferenceSnapshot> ReadAsync(string kind, AssignedFieldSite? site, CancellationToken ct)
    {
        if (site is null) return new("not-assigned", []);
        return kind == "issued-materials"
            ? await IssuedAsync(site, ct)
            : await EstimatedAsync(site, ct);
    }

    private async Task<FieldReferenceSnapshot> IssuedAsync(AssignedFieldSite site, CancellationToken ct)
    {
        if (contracts.Get<IMaterialsOnSite>() is not ModuleContractAnswer<IMaterialsOnSite>.Connected source)
            return new("not-connected", []);
        var hints = await source.Contract.ReadFieldHintsAsync(site, ct);
        var names = await NamesAsync(hints.Select(h => h.NomenclatureId), ct);
        if (names is null) return new("not-configured", []);
        var items = hints.Where(h => names.ContainsKey(h.NomenclatureId)).OrderBy(h => h.NomenclatureId)
            .Select(h => new Issued(h.NomenclatureId, names[h.NomenclatureId], h.Unit)).ToArray();
        return new("ok", items);
    }

    private async Task<FieldReferenceSnapshot> EstimatedAsync(AssignedFieldSite site, CancellationToken ct)
    {
        if (contracts.Get<IPlannedWorks>() is not ModuleContractAnswer<IPlannedWorks>.Connected source)
            return new("not-connected", []);
        var hints = await source.Contract.ReadFieldHintsAsync(site, ct);
        var names = await NamesAsync(hints.Select(h => h.NomenclatureId), ct);
        if (names is null) return new("not-configured", []);
        var items = hints.Where(h => names.ContainsKey(h.NomenclatureId))
            .OrderBy(h => h.PlanItemId).ThenBy(h => h.NomenclatureId)
            .Select(h => new Estimated(h.NomenclatureId, h.PlanItemId, names[h.NomenclatureId])).ToArray();
        return new("ok", items);
    }

    private async Task<Dictionary<Guid, string?>?> NamesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToArray();
        if (wanted.Length == 0) return [];
        var refs = await catalog.RefsAsync(FunctionalTag.RoleNomenclature, wanted, ct);
        return refs?.Where(r => !r.Archived).ToDictionary(r => r.Id, r => r.DisplayName);
    }
}
