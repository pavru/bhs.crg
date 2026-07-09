using BHS.CRG.Application.DataSets;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Присваивает стабильные <see cref="GostGroupingGroup.Id"/> группам новой группировки, перенося
/// их из предыдущей при перераспознавании (issue #28, линчпин Фазы 1): Cover/TitlePage — по Kind;
/// Document — по максимальному пересечению страниц с существующей группой. Непарные группы получают
/// новый Guid. Стабильный id заменяет дрейфующий firstPageIndex как ключ производных источников-таблиц,
/// устраняя их осиротение при перераспознавании (P1).
/// </summary>
public static class GostStableIds
{
    public static GostGroupingData Assign(GostGroupingData fresh, GostGroupingData? existing)
    {
        var existingGroups = existing?.Groups?.ToList() ?? [];
        var claimed = new HashSet<Guid>();
        var result = new List<GostGroupingGroup>(fresh.Groups.Count);

        foreach (var g in fresh.Groups)
        {
            var id = Guid.Empty;

            if (g.Kind is GostGroupKind.Cover or GostGroupKind.TitlePage)
            {
                var m = existingGroups.FirstOrDefault(e =>
                    e.Kind == g.Kind && e.Id != Guid.Empty && !claimed.Contains(e.Id));
                if (m is not null) id = m.Id;
            }
            else
            {
                var pages = g.Pages.Select(p => p.PageIndex).ToHashSet();
                GostGroupingGroup? best = null;
                var bestOverlap = 0;
                foreach (var e in existingGroups)
                {
                    if (e.Kind != GostGroupKind.Document || e.Id == Guid.Empty || claimed.Contains(e.Id)) continue;
                    var overlap = e.Pages.Count(p => pages.Contains(p.PageIndex));
                    if (overlap > bestOverlap) { bestOverlap = overlap; best = e; }
                }
                if (best is not null && bestOverlap > 0) id = best.Id;
            }

            if (id == Guid.Empty) id = Guid.NewGuid();
            else claimed.Add(id);

            result.Add(g with { Id = id });
        }

        return fresh with { Groups = result };
    }

    /// <summary>Гарантирует, что у каждой группы есть Id (для группировок, прочитанных без id) — не меняет существующие.</summary>
    public static GostGroupingData EnsureIds(GostGroupingData data)
    {
        if (data.Groups.All(g => g.Id != Guid.Empty)) return data;
        var result = data.Groups
            .Select(g => g.Id == Guid.Empty ? g with { Id = Guid.NewGuid() } : g)
            .ToList();
        return data with { Groups = result };
    }
}
