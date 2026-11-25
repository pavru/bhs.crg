using System.Data;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Exchange;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Exchange;

internal sealed class MaterialsOnSiteContract(CostsDbContext db) : IMaterialsOnSite
{
    public async Task<ContractAnswer<MaterialsOnSite>> ReadAsync(
        ModuleReader reader, Guid constructionId, CancellationToken ct = default)
    {
        if (ContractAnswer.Refusal<MaterialsOnSite>(reader, CostsModule.MaterialsRead, CostsModule.MaterialsHint)
            is { } refusal)
            return refusal;

        // Перечень и число несопоставленных строк должны видеть одну версию накладных.
        await using var snapshot = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct)
            : null;
        var report = await IssuedMaterials.ReadAsync(db, constructionId, ct);
        if (reader.Allows(CostsModule.MaterialsRead))
        {
            var items = report.Items.Select(item =>
                new MaterialOnSite(item.NomenclatureId, item.Unit, item.Quantity, item.First, item.Last));
            return ContractAnswer.Shown<MaterialsOnSite>(new MaterialsOnSite.Full([.. items], report.UnmatchedLines));
        }

        var nomenclatureIds = report.Items.Select(item => item.NomenclatureId).Distinct();
        return ContractAnswer.Shown<MaterialsOnSite>(new MaterialsOnSite.Hint([.. nomenclatureIds]));
    }
}
