using BHS.CRG.Modules.Plan.Data;

namespace BHS.CRG.Modules.Plan.Distribution;

/// <summary>
/// Раздел ЛСР ключом словаря: в отличие от строки, принимает и <c>null</c> — строки вне разделов (у них одно
/// правило на группу).
/// </summary>
public readonly record struct LsrKey(string? Code);

/// <summary>Строка — то, что нужно сводке: раздел ЛСР и объём в единице строки.</summary>
public sealed record LineInput(Guid RowId, string? LsrSectionCode, decimal? SourceVolume);

/// <summary>Действующее распределение одной строки.</summary>
/// <param name="Rest">Остаток «стройка в целом» в единице строки — у строки с частями; иначе <c>null</c>.</param>
/// <param name="Targets">Разделы стройки строки — для отбора по цели.</param>
/// <param name="Whole">Часть объёма строки идёт в «стройку в целом»: правилом или остатком частей.</param>
public sealed record LineState(
    Guid RowId, LineStatus Status, LineSource Source, IReadOnlyList<StoredPart> Parts, decimal? Rest,
    IReadOnlyList<Guid> Targets, bool Whole);

/// <param name="Exceptions">Строк раздела со своими частями — правило к ним не применяется.</param>
/// <param name="Undistributed">Строк раздела без решения: нет ни частей, ни правила.</param>
public sealed record SectionState(LsrKey Key, DistributionTarget? Rule, int Rows, int Exceptions, int Undistributed);

/// <summary>Распределение редакции целиком: строки, разделы ЛСР и счётчики.</summary>
public sealed record DistributionSummary(
    IReadOnlyList<LineState> Lines, IReadOnlyDictionary<LsrKey, SectionState> Sections, DistributionTally Tally);

/// <summary>
/// Сводка распределения редакции из её строк, правил и частей (change <c>plan-10-distribution</c>, ТЗ PLAN-5.1) —
/// одним правилом для ответа распределения и разделов ЛСР; счётчики списка смет считает база
/// (<see cref="DistributionCounts" />) тем же правилом <see cref="DistributionMath.Status" />.
/// </summary>
public static class DistributionLines
{
    public static DistributionSummary Evaluate(
        IReadOnlyList<LineInput> rows, IEnumerable<DistributionRule> rules, IEnumerable<DistributionPart> parts) =>
        Evaluate(rows, rules.ToDictionary(r => new LsrKey(r.LsrSectionCode), r => r.Target),
            parts.GroupBy(p => p.RowId).ToDictionary(g => g.Key,
                g => (IReadOnlyList<StoredPart>)[.. g.OrderByDescending(p => p.Quantity).Select(p => new StoredPart(p.SectionId, p.Quantity))]));

    /// <param name="ruleOf">Правило раздела ЛСР; раздела нет в словаре — правила нет.</param>
    /// <param name="partsOf">Части строк, у которых они есть.</param>
    public static DistributionSummary Evaluate(
        IReadOnlyList<LineInput> rows, IReadOnlyDictionary<LsrKey, DistributionTarget> ruleOf,
        IReadOnlyDictionary<Guid, IReadOnlyList<StoredPart>> partsOf)
    {

        var lines = new List<LineState>(rows.Count);
        var sections = new Dictionary<LsrKey, SectionState>();
        var tally = DistributionTally.Empty;
        foreach (var row in rows)
        {
            var key = new LsrKey(row.LsrSectionCode);
            var rule = ruleOf.GetValueOrDefault(key);
            var own = partsOf.GetValueOrDefault(row.RowId);
            var sum = own?.Sum(p => p.Quantity);
            var status = DistributionMath.Status(row.SourceVolume, sum, rule);
            var source = DistributionMath.Source(own is not null, rule);
            var rest = own is null ? (decimal?)null : Math.Max(0m, (row.SourceVolume ?? 0m) - sum!.Value);
            IReadOnlyList<Guid> targets = own is not null ? [.. own.Select(p => p.SectionId)]
                : rule is { Kind: DistributionTargetKind.Section, SectionId: { } section } ? [section]
                : [];
            var whole = own is not null ? rest > 0m : rule?.Kind == DistributionTargetKind.Whole;
            lines.Add(new LineState(row.RowId, status, source, own ?? [], rest, targets, whole));
            tally = tally.Add(status);

            var seen = sections.GetValueOrDefault(key) ?? new SectionState(key, rule, 0, 0, 0);
            sections[key] = seen with
            {
                Rows = seen.Rows + 1,
                Exceptions = seen.Exceptions + (own is null ? 0 : 1),
                Undistributed = seen.Undistributed + (status == LineStatus.Undistributed ? 1 : 0),
            };
        }
        return new DistributionSummary(lines, sections, tally);
    }
}
