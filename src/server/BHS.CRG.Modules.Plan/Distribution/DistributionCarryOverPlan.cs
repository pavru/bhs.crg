using BHS.CRG.Modules.Plan.Data;

namespace BHS.CRG.Modules.Plan.Distribution;

/// <summary>Строка редакции — то, по чему перенос её сопоставляет и чем позиция отчёта её назовёт.</summary>
public sealed record CarryRow(
    Guid Id, int Order, string? LsrSectionCode, string? LsrSectionName, string? RateCode, string? NumberInSection,
    string? Number, string Name, decimal? SourceVolume, string? SourceUnit)
{
    public static CarryRow Of(EstimateRow row) => new(row.Id, row.Order, row.LsrSectionCode, row.LsrSectionName,
        row.RateCode, row.NumberInSection, row.Number, row.Name, row.SourceVolume, row.SourceUnit);

    public CarryOverSource Source() =>
        new(Id, LsrSectionCode, LsrSectionName, RateCode, NumberInSection, Number, Name, SourceVolume, SourceUnit);
}

/// <summary>Распределение источника: строки, правила разделов ЛСР и части строк.</summary>
public sealed record CarrySource(
    IReadOnlyList<CarryRow> Rows, IReadOnlyDictionary<LsrKey, DistributionTarget> Rules,
    IReadOnlyDictionary<Guid, IReadOnlyList<CarriedPart>> Parts);

/// <summary>Позиция отчёта к записи.</summary>
public sealed record CarryItemDraft(
    CarryOverCategory Category, CarryOverSubject Subject, CarryOverSource Source, IReadOnlyList<CarriedPart> Parts,
    DistributionTarget? Rule, Guid? RowId, decimal? NewVolume, IReadOnlyList<Guid> Candidates);

/// <summary>Что записать у новой редакции: правила, части строк, позиции отчёта и число перенесённых без пометки.</summary>
public sealed record CarryPlan(
    IReadOnlyList<(LsrKey Section, DistributionTarget Target)> Rules,
    IReadOnlyList<(Guid RowId, IReadOnlyList<CarriedPart> Parts)> Parts,
    IReadOnlyList<CarryItemDraft> Items, int Carried)
{
    public int Count(CarryOverCategory category) => Items.Count(i => i.Category == category);
}

/// <summary>
/// Сопоставление распределения источника с новой редакцией при повторном импорте (change
/// <c>plan-10-distribution</c>, «Перенос» в <c>design.md</c>, ТЗ PLAN-5.1, COST-13) — без базы.
///
/// <para><b>Раздел ЛСР</b> — по названию, если оно однозначно в обеих редакциях: номер главы в файле сдвигается от
/// вставленной главы и ключом служить не может. Названия нет или оно повторяется — по номеру главы, если у главы с
/// этим номером в новой редакции название не занято другим разделом источника; такой раздел помечается
/// «проверьте» (<c>shifted</c>). Иначе раздел не сопоставлен: его правило и части строк — в <c>lost</c>.</para>
///
/// <para><b>Строка</b> внутри сопоставленного раздела — по шифру и порядку его вхождения (k-я строка с шифром X →
/// k-я строка с X), а не по номеру в разделе: вставка позиции с другим шифром ничего не сдвигает, а повтор шифра не
/// путает соседние строки. Число строк с шифром разное — <c>ambiguous</c> с кандидатами; части больше нового объёма
/// — <c>overflow</c>. Так каждая строка новой редакции получает не больше одной строки источника.</para>
///
/// <para><b>Единица строки сменилась</b> («100 м» → «м») — тоже <c>overflow</c>: части хранятся в единице строки, и
/// «7 · 100 м» на новой строке стали бы «7 м», а остаток молча ушёл бы в «стройку в целом». Строку распределяют
/// заново в окне строки.</para>
///
/// <para><b>Строки вне разделов ЛСР</b> (код <c>null</c>) сопоставляются друг с другом точно: это не номер главы, и
/// сдвинуться ему не от чего.</para>
///
/// <para>Переносятся только части строк: строку без частей переносит правило её раздела.</para>
/// </summary>
public static class DistributionCarryOverPlan
{
    public static CarryPlan Build(CarrySource source, IReadOnlyList<CarryRow> target)
    {
        var from = Sections(source.Rows);
        var to = Sections(target);
        var match = MatchSections(from, to);

        var rules = new List<(LsrKey, DistributionTarget)>();
        var parts = new List<(Guid, IReadOnlyList<CarriedPart>)>();
        var items = new List<CarryItemDraft>();
        var carried = 0;

        foreach (var section in from)
        {
            var rule = source.Rules.GetValueOrDefault(section.Key);
            var sectionSource = new CarryOverSource(null, section.Key.Code, section.Name);
            var parted = section.Rows.Where(r => source.Parts.ContainsKey(r.Id)).ToList();
            if (!match.TryGetValue(section.Key, out var found))
            {
                if (rule is not null)
                    items.Add(new(CarryOverCategory.Lost, CarryOverSubject.Rule, sectionSource, [], rule, null, null, []));
                items.AddRange(parted.Select(r =>
                    new CarryItemDraft(CarryOverCategory.Lost, CarryOverSubject.Row, r.Source(), source.Parts[r.Id], null, null, null, [])));
                continue;
            }

            var (into, byNumber) = found;
            if (rule is not null)
            {
                rules.Add((into.Key, rule));
                if (byNumber)
                    items.Add(new(CarryOverCategory.Shifted, CarryOverSubject.Rule, sectionSource, [], rule, null, null, []));
            }

            var fromByCode = section.Rows.GroupBy(Code).ToDictionary(g => g.Key, g => g.ToList());
            var toByCode = into.Rows.GroupBy(Code).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var row in parted)
            {
                var own = source.Parts[row.Id];
                var code = Code(row);
                var candidates = toByCode.GetValueOrDefault(code) ?? [];
                if (candidates.Count == 0)
                {
                    items.Add(new(CarryOverCategory.Lost, CarryOverSubject.Row, row.Source(), own, null, null, null, []));
                    continue;
                }
                var peers = fromByCode[code];
                if (peers.Count != candidates.Count)
                {
                    items.Add(new(CarryOverCategory.Ambiguous, CarryOverSubject.Row, row.Source(), own, null, null, null,
                        [.. candidates.Select(c => c.Id)]));
                    continue;
                }

                var next = candidates[peers.IndexOf(row)];
                if (next.SourceVolume is not > 0m || own.Sum(p => p.Quantity) > next.SourceVolume || !SameUnit(row, next))
                {
                    items.Add(new(CarryOverCategory.Overflow, CarryOverSubject.Row, row.Source(), own, null, next.Id,
                        next.SourceVolume, []));
                    continue;
                }

                parts.Add((next.Id, own));
                if (byNumber || next.NumberInSection != row.NumberInSection)
                    items.Add(new(CarryOverCategory.Shifted, CarryOverSubject.Row, row.Source(), own, null, next.Id, null, []));
                else
                    carried++;
            }
        }

        return new CarryPlan(rules, parts, items, carried);
    }

    private sealed record Section(LsrKey Key, string? Name, List<CarryRow> Rows);

    private static List<Section> Sections(IEnumerable<CarryRow> rows) =>
    [
        .. rows.OrderBy(r => r.Order).GroupBy(r => new LsrKey(r.LsrSectionCode))
            .Select(g => new Section(g.Key, g.First().LsrSectionName, [.. g])),
    ];

    /// <summary>Раздел источника → раздел новой редакции и пометка «сопоставлен по номеру главы».</summary>
    private static Dictionary<LsrKey, (Section Into, bool ByNumber)> MatchSections(List<Section> from, List<Section> to)
    {
        var match = new Dictionary<LsrKey, (Section, bool)>();
        var taken = new HashSet<LsrKey>();
        static string? Name(Section s) => string.IsNullOrWhiteSpace(s.Name) ? null : s.Name.Trim();
        static int Count(List<Section> list, string name) =>
            list.Count(s => string.Equals(Name(s), name, StringComparison.OrdinalIgnoreCase));

        if (from.FirstOrDefault(s => s.Key.Code is null) is { } outside && to.FirstOrDefault(s => s.Key.Code is null) is { } rest)
        {
            match[outside.Key] = (rest, false);
            taken.Add(rest.Key);
        }

        // Разделы ЛСР — только с разделами: строкам вне разделов не достаться главе и наоборот.
        from = [.. from.Where(s => s.Key.Code is not null)];
        to = [.. to.Where(s => s.Key.Code is not null)];
        foreach (var section in from)
        {
            if (Name(section) is not { } name || Count(from, name) != 1 || Count(to, name) != 1) continue;
            var into = to.Single(s => string.Equals(Name(s), name, StringComparison.OrdinalIgnoreCase));
            match[section.Key] = (into, false);
            taken.Add(into.Key);
        }

        foreach (var section in from.Where(s => !match.ContainsKey(s.Key)))
        {
            // По номеру — только когда название не говорит ничего: его нет или оно повторяется. Однозначное название,
            // которого в новой редакции нет, — раздел убрали или переименовали, и угадывать его по номеру нельзя.
            if (Name(section) is { } own && Count(from, own) == 1 && Count(to, own) <= 1) continue;
            if (to.FirstOrDefault(s => s.Key == section.Key) is not { } into || taken.Contains(into.Key)) continue;
            // Название этой главы — у другого раздела источника: глава «его», а не этого, сколько бы номер ни совпадал.
            if (Name(into) is { } name && from.Any(s => s != section
                    && string.Equals(Name(s), name, StringComparison.OrdinalIgnoreCase))) continue;
            match[section.Key] = (into, true);
            taken.Add(into.Key);
        }
        return match;
    }

    /// <summary>Единица строки та же — части в ней значат то же количество.</summary>
    private static bool SameUnit(CarryRow from, CarryRow to) =>
        string.Equals(from.SourceUnit?.Trim(), to.SourceUnit?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Шифр строки — ключ внутри раздела; строка без шифра узнаётся по наименованию.</summary>
    private static string Code(CarryRow row) =>
        string.IsNullOrWhiteSpace(row.RateCode) ? "\u0000" + row.Name.Trim() : row.RateCode.Trim();
}
