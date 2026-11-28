using System.Data;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Exchange;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Exchange;

internal sealed class MaterialsOnSiteContract(CostsDbContext db) : IMaterialsOnSite
{
    public async Task<IReadOnlyList<MaterialOnSiteHint>> ReadFieldHintsAsync(
        AssignedFieldSite site, CancellationToken ct = default) =>
        await IssuedMaterials.ReadHintAsync(db, site.ConstructionId, ct);

    public async Task<ContractAnswer<IReadOnlyList<MaterialOnSiteHint>>> ReadHintAsync(
        ModuleReader reader, Guid constructionId, CancellationToken ct = default) =>
        ContractAnswer.Refusal<IReadOnlyList<MaterialOnSiteHint>>(reader, CostsModule.MaterialsHint, CostsModule.MaterialsRead)
        ?? ContractAnswer.Shown(await IssuedMaterials.ReadHintAsync(db, constructionId, ct));

    public async Task<ContractAnswer<MaterialsOnSite>> ReadAsync(
        ModuleReader reader, Guid constructionId, CancellationToken ct = default)
    {
        if (ContractAnswer.Refusal<MaterialsOnSite>(reader, CostsModule.MaterialsRead, CostsModule.MaterialsHint)
            is { } refusal)
            return refusal;

        if (!reader.Allows(CostsModule.MaterialsRead))
        {
            var ids = await IssuedMaterials.ReadNomenclatureIdsAsync(db, constructionId, ct);
            return ContractAnswer.Shown<MaterialsOnSite>(new MaterialsOnSite.Hint(ids));
        }

        // Перечень и число несопоставленных строк должны видеть одну версию накладных.
        await using var snapshot = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct)
            : null;
        var report = await IssuedMaterials.ReadAsync(db, constructionId, ct);
        var items = report.Items.Select(item =>
            new MaterialOnSite(item.NomenclatureId, item.Unit, item.Quantity, item.First, item.Last));
        return ContractAnswer.Shown<MaterialsOnSite>(new MaterialsOnSite.Full([.. items], report.UnmatchedLines));
    }
}
