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
    /// <param name="carryUserData">true — переносить пользовательскую разметку (тэги + табличное сырьё)
    /// с прежних групп по стабильному id: нужно при полном РЕ-РАСПОЗНАВАНИИ (свежие группы приходят без
    /// тэгов, иначе они бы потерялись). false (ручная правка ApplyGrouping) — тэги во fresh авторитетны
    /// (в т.ч. снятие тэга), переносим только Id.</param>
    public static GostGroupingData Assign(GostGroupingData fresh, GostGroupingData? existing, bool carryUserData = false)
    {
        var existingGroups = existing?.Groups?.ToList() ?? [];
        var claimed = new HashSet<Guid>();
        var result = new List<GostGroupingGroup>(fresh.Groups.Count);

        foreach (var g in fresh.Groups)
        {
            var id = Guid.Empty;
            GostGroupingGroup? matched = null;

            if (g.Kind is GostGroupKind.Cover or GostGroupKind.TitlePage)
            {
                var m = existingGroups.FirstOrDefault(e =>
                    e.Kind == g.Kind && e.Id != Guid.Empty && !claimed.Contains(e.Id));
                if (m is not null) { id = m.Id; matched = m; }
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
                if (best is not null && bestOverlap > 0) { id = best.Id; matched = best; }
            }

            if (id == Guid.Empty) id = Guid.NewGuid();
            else claimed.Add(id);

            // Перенос пользовательской разметки/сырья по стабильному id (issue #42) — только при полном
            // ре-распознавании (carryUserData): тэги переносим (ручной выбор типа таблицы не теряется),
            // табличное сырьё (TableData/Columns) тоже, но если состав страниц изменился — помечаем stale.
            // Порог доминирования: сырьё переносим только при существенном пересечении (иначе чужая группа
            // унаследовала бы таблицу). При ручной правке (carryUserData=false) тэги во fresh авторитетны.
            var carried = g with { Id = id };
            if (matched is not null && carryUserData)
            {
                var freshPages = g.Pages.Select(p => p.PageIndex).ToHashSet();
                var existPages = matched.Pages.Select(p => p.PageIndex).ToHashSet();
                var samePages = freshPages.SetEquals(existPages);
                var overlap = freshPages.Count(existPages.Contains);
                var dominant = overlap * 2 >= Math.Max(freshPages.Count, existPages.Count); // ≥половины большей группы
                carried = carried with { Tags = matched.Tags ?? g.Tags };
                if (dominant && !string.IsNullOrEmpty(matched.TableData))
                    carried = carried with { TableData = matched.TableData, TableColumns = matched.TableColumns, TableStale = matched.TableStale || !samePages };
                // Извлечённый текст документа (issue #51) — тот же перенос, что TableData.
                if (dominant && !string.IsNullOrEmpty(matched.SheetText))
                    carried = carried with { SheetText = matched.SheetText, SheetTextStale = matched.SheetTextStale || !samePages };
            }
            result.Add(carried);
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
