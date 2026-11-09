using BHS.CRG.Modules.Plan.Data;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Distribution;

/// <summary>Счётчики распределения редакции для ответа сметы (D17 зонтика): полоса «распределено N из M» и чипы.</summary>
/// <param name="CarryOverPending">Позиций отчёта переноса, которые ждут решения.</param>
/// <param name="ChangedAt">Когда распределение меняли последний раз; <c>null</c> — не распределяли.</param>
public sealed record RevisionDistributionCounts(DistributionTally Tally, int CarryOverPending, DateTimeOffset? ChangedAt);

/// <summary>
/// Счётчики распределения многих редакций сразу — для списка смет (change <c>plan-10-distribution</c>, задача
/// 1.7): строк в редакциях тысячи, поэтому строки не загружаются — база считает строки по разделам ЛСР и суммы
/// частей по строкам с частями, а состояние даёт то же правило, что у сводки (<see cref="DistributionMath.Status" />).
/// Совпадение с <see cref="DistributionLines" /> сверяет тест.
///
/// <para>⚠️ Счётчики ОДНОЙ редакции для показа, а не план стройки — тот читается только <see cref="PlanRows.Current" />.</para>
/// </summary>
internal static class DistributionCounts
{
    /// <param name="onSection">Редакции смет на разделе стройки: их строки целиком в разделе сметы — распределены.</param>
    public static async Task<Dictionary<Guid, RevisionDistributionCounts>> ForRevisionsAsync(
        PlanDbContext db, IReadOnlyCollection<Guid> revisionIds, IReadOnlySet<Guid> onSection, CancellationToken ct)
    {
        var ids = revisionIds.ToList();
        var groups = await db.EstimateRows.AsNoTracking()
            .Where(r => ids.Contains(r.RevisionId))
            .GroupBy(r => new { r.RevisionId, r.LsrSectionCode })
            .Select(g => new { g.Key.RevisionId, g.Key.LsrSectionCode, Rows = g.Count() })
            .ToListAsync(ct);
        var parted = await (
                from sum in db.DistributionParts.Where(p => ids.Contains(p.RevisionId))
                    .GroupBy(p => p.RowId).Select(g => new { RowId = g.Key, Sum = g.Sum(p => p.Quantity) })
                join row in db.EstimateRows on sum.RowId equals row.Id
                select new { row.RevisionId, row.LsrSectionCode, sum.Sum, row.SourceVolume })
            .ToListAsync(ct);
        var rules = (await db.DistributionRules.AsNoTracking().Where(r => ids.Contains(r.RevisionId)).ToListAsync(ct))
            .ToDictionary(r => (r.RevisionId, new LsrKey(r.LsrSectionCode)), r => r.Target);
        var changed = await db.RevisionDistributions.AsNoTracking().Where(h => ids.Contains(h.RevisionId))
            .ToDictionaryAsync(h => h.RevisionId, h => h.ChangedAt, ct);
        var pending = await db.CarryOverItems.AsNoTracking()
            .Where(i => ids.Contains(i.RevisionId) && i.Resolution == null && i.Category != CarryOverCategory.Shifted)
            .GroupBy(i => i.RevisionId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var partsOf = parted.ToLookup(p => (p.RevisionId, new LsrKey(p.LsrSectionCode)));
        var tallies = ids.ToDictionary(id => id, _ => DistributionTally.Empty);
        foreach (var group in groups)
        {
            var key = (group.RevisionId, new LsrKey(group.LsrSectionCode));
            if (onSection.Contains(group.RevisionId))
            {
                tallies[group.RevisionId] = tallies[group.RevisionId].Add(LineStatus.Distributed, group.Rows);
                continue;
            }
            var rule = rules.GetValueOrDefault(key);
            var own = partsOf[key].ToList();
            var tally = tallies[group.RevisionId];
            foreach (var row in own) tally = tally.Add(DistributionMath.Status(row.SourceVolume, row.Sum, rule));
            tallies[group.RevisionId] = tally.Add(DistributionMath.Status(null, null, rule), group.Rows - own.Count);
        }

        return ids.ToDictionary(id => id, id => new RevisionDistributionCounts(
            tallies[id], onSection.Contains(id) ? 0 : pending.GetValueOrDefault(id),
            changed.TryGetValue(id, out var at) ? at : null));
    }
}
