using BHS.CRG.Modules.Plan.Data;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Offers;

internal static class PriceListPricing
{
    public static PriceListEntry? Effective(IEnumerable<PriceListEntry> history, DateOnly date) =>
        history.Where(e => e.ValidFrom <= date).MaxBy(e => e.ValidFrom);

    public static async Task<Dictionary<(Guid WorkTypeId, Guid UnitId), PriceListEntry>> ForWorksAsync(
        PlanDbContext db, Guid[] workTypeIds, DateOnly date, CancellationToken ct)
    {
        var history = await db.PriceListEntries.AsNoTracking()
            .Where(e => workTypeIds.Contains(e.WorkTypeId) && e.ValidFrom <= date).ToListAsync(ct);
        return history.GroupBy(e => (e.WorkTypeId, e.UnitId)).ToDictionary(g => g.Key, g => Effective(g, date)!);
    }
}
