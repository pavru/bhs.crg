using BHS.CRG.Modules.Plan.Data;

namespace BHS.CRG.Modules.Plan.Offers;

internal sealed record RebaseSection(string? Code, string? Name, IReadOnlyList<EstimateRow> Rows)
{
    internal (bool Outside, string? Name) Key => Code is null ? (true, null) : (false, OfferRebaseMatching.NameKey(Name));
}

internal sealed record RebaseSections(
    IReadOnlyList<RebaseSection> Before, IReadOnlyList<RebaseSection> After, bool Paired);

internal sealed record RebaseRows(IReadOnlyList<EstimateRow> Before, IReadOnlyList<EstimateRow> After, bool Paired);

/// <summary>Порядковые номера из ЛСР годятся для показа; вставка строки или главы меняет их.</summary>
internal static class OfferRebaseMatching
{
    internal static IEnumerable<RebaseSections> Sections(
        IEnumerable<EstimateRow> source, IEnumerable<EstimateRow> target)
    {
        var before = BySection(source);
        var after = BySection(target);
        foreach (var key in before.Select(g => g.Key).Union(after.Select(g => g.Key)))
        {
            var old = before[key].ToList();
            var next = after[key].ToList();
            yield return new(old, next, old.Count == 1 && next.Count == 1 && (key.Outside || key.Name is not null));
        }
    }

    internal static IEnumerable<RebaseRows> Rows(RebaseSections sections)
    {
        if (!sections.Paired)
        {
            yield return new(sections.Before.SelectMany(s => s.Rows).ToList(),
                sections.After.SelectMany(s => s.Rows).ToList(), false);
            yield break;
        }

        var before = sections.Before[0].Rows.ToLookup(RowKey.Of);
        var after = sections.After[0].Rows.ToLookup(RowKey.Of);
        foreach (var key in before.Select(g => g.Key).Union(after.Select(g => g.Key)))
        {
            var old = before[key].ToList();
            var next = after[key].ToList();
            if (old.Count == 1 && next.Count == 1)
            {
                yield return new(old, next, true);
                continue;
            }

            // Повторы различаем только по названию, если оно однозначно с обеих сторон.
            var namedBefore = old.ToLookup(r => NameKey(r.Name));
            var namedAfter = next.ToLookup(r => NameKey(r.Name));
            var paired = new HashSet<Guid>();
            foreach (var group in namedBefore)
            {
                var candidates = namedAfter[group.Key];
                if (group.Key is null || group.Count() != 1 || candidates.Count() != 1) continue;
                var from = group.Single();
                var to = candidates.Single();
                paired.Add(from.Id);
                paired.Add(to.Id);
                yield return new([from], [to], true);
            }

            var remainingBefore = old.Where(r => !paired.Contains(r.Id)).ToList();
            var remainingAfter = next.Where(r => !paired.Contains(r.Id)).ToList();
            if (remainingBefore.Count > 0 || remainingAfter.Count > 0)
                yield return new(remainingBefore, remainingAfter, false);
        }
    }

    private static ILookup<(bool Outside, string? Name), RebaseSection> BySection(IEnumerable<EstimateRow> rows) =>
        rows.GroupBy(r => r.LsrSectionCode)
            .Select(g => new RebaseSection(g.Key, g.First().LsrSectionName, g.ToList())).ToLookup(s => s.Key);

    internal static string? NameKey(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private sealed record RowKey(Guid? PlanItemId, EstimateRowKind Kind, string? Rate, string? Name)
    {
        internal static RowKey Of(EstimateRow row)
        {
            var rate = NameKey(row.RateCode);
            return new(row.PlanItemId, row.Kind, rate, rate is null ? NameKey(row.Name) : null);
        }
    }
}
