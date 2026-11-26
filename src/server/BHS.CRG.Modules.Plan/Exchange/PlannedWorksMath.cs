using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Plan.Distribution;

namespace BHS.CRG.Modules.Plan.Exchange;

/// <summary>Строка действующей редакции — то, что нужно «Плановым работам».</summary>
/// <param name="PlanItemId">Позиция перечня; <c>null</c> — строка-материал (D2 <c>plan-31</c>).</param>
/// <param name="Volume">Объём в единице классификатора; у строки-материала — <c>null</c>.</param>
/// <param name="SourceVolume">Объём как в файле — в нём хранятся части распределения.</param>
internal sealed record PlannedRow(
    Guid Id, Guid RevisionId, Guid? PlanItemId, Guid? UnitId, decimal? Volume, decimal? SourceVolume,
    string? LsrSectionCode, decimal? Total);

/// <summary>Материал строки: позиция номенклатуры (или пусто, пока не сопоставлен), количество и стоимость.</summary>
internal sealed record PlannedRowMaterial(Guid RowId, Guid? NomenclatureId, decimal Quantity, string? Unit, decimal? Amount);

/// <summary>Трудозатраты строки: чел.-ч на её объём.</summary>
internal sealed record PlannedRowLabor(Guid RowId, decimal ManHours);

/// <summary>
/// Доли строки по разделам стройки (ТЗ PLAN-5.1): какая часть её объёма лежит на каком разделе. Остаток —
/// «стройка в целом» (<see cref="Whole" />); <see cref="Undistributed" /> — решения о строке ещё не было.
/// </summary>
internal sealed record RowShare(IReadOnlyDictionary<Guid, decimal> Sections, bool Undistributed)
{
    public decimal Whole => 1m - Sections.Values.Sum();

    /// <summary>Смета на разделе или правило раздела ЛСР: строка целиком на разделе.</summary>
    public static RowShare OnSection(Guid section) => new(new Dictionary<Guid, decimal> { [section] = 1m }, false);

    public static readonly RowShare WholeConstruction = new(new Dictionary<Guid, decimal>(), false);

    public static readonly RowShare NotDecided = new(new Dictionary<Guid, decimal>(), true);

    /// <summary>Доля строки, которую берёт ответ: без отбора — вся, с отбором — доля раздела.</summary>
    public decimal Weight(Guid? section) => section is { } s ? Sections.GetValueOrDefault(s) : 1m;

    /// <summary>
    /// Доли по действующему распределению строки — тем же правилом, что ответ распределения
    /// (<see cref="DistributionLines" />): части, иначе правило её раздела ЛСР, иначе «не распределено».
    /// </summary>
    /// <remarks>
    /// Доля части — её количество на объём строки, а если части больше объёма или объёма в смете нет — на сумму
    /// частей: доли разделов вместе не больше единицы, и «стройка в целом» не уходит в минус. Пути записи
    /// частей больше объёма не пускают; знаменатель держит ответ и без них.
    /// </remarks>
    public static RowShare Of(LineState line, decimal? sourceVolume)
    {
        if (line.Source == LineSource.Parts)
        {
            var whole = Math.Max(sourceVolume ?? 0m, line.Parts.Sum(p => p.Quantity));
            if (whole > 0m)
                return new RowShare(line.Parts.GroupBy(p => p.SectionId)
                    .ToDictionary(g => g.Key, g => g.Sum(p => p.Quantity) / whole), false);
            return NotDecided;
        }
        if (line.Source == LineSource.Rule)
            return line.Targets is [var section] ? OnSection(section) : WholeConstruction;
        return NotDecided;
    }
}

/// <summary>
/// Арифметика «Плановых работ» (change <c>plan-23-planned-works-contract</c>) — без базы: строки, их доли по
/// разделам, материалы и трудозатраты превращаются в части ответа.
///
/// <para><b>Отбор по разделу</b> берёт у каждой строки долю раздела — и с той же долей её материалы,
/// стоимость и чел.-ч; строки без доли в ответ не входят. Норма — отношение долей, то есть та же норма.</para>
///
/// <para><b>Неизвестное — не ноль.</b> Стоимость, которой в смете нет, и норма, которой смета не несёт, —
/// <c>null</c>: частичная сумма читалась бы как полная, а ноль — как «бесплатно» или «без труда».</para>
/// </summary>
internal static class PlannedWorksMath
{
    private const int Quantities = 6;
    private const int Money = 2;

    public static IReadOnlyList<PlannedItem> Items(
        IReadOnlyList<PlannedRow> rows, IReadOnlyDictionary<Guid, RowShare> shares, Guid? section) =>
        [.. ByPlanItem(rows).Select(g => section is { } s ? OnSection(g.Key, g, shares, s) : Whole(g.Key, g, shares))];

    private static PlannedItem OnSection(Guid item, IEnumerable<PlannedRow> rows, IReadOnlyDictionary<Guid, RowShare> shares, Guid section)
    {
        var list = rows.ToList();
        var volume = list.Sum(r => Round(Volume(r) * shares[r.Id].Weight(section), Quantities));
        var missing = WithoutVolume(list);
        return new PlannedItem(item, list[0].UnitId!.Value, volume, [new PlannedShare(section, volume)], 0m, missing)
        {
            HasVolume = missing < list.Count,
        };
    }

    private static PlannedItem Whole(Guid item, IEnumerable<PlannedRow> rows, IReadOnlyDictionary<Guid, RowShare> shares)
    {
        var list = rows.ToList();
        var bySection = new Dictionary<Guid, decimal>();
        decimal whole = 0m, undistributed = 0m;
        foreach (var row in list)
        {
            var volume = Volume(row);
            var share = shares[row.Id];
            var onSections = 0m;
            foreach (var (s, fraction) in share.Sections)
            {
                var part = Round(volume * fraction, Quantities);
                bySection[s] = bySection.GetValueOrDefault(s) + part;
                onSections += part;
            }
            // Остаток — разностью, а не долей: сумма распределения равна объёму без ошибки округления.
            whole += volume - onSections;
            if (share.Undistributed) undistributed += volume;
        }

        List<PlannedShare> distribution =
            [.. bySection.Where(p => p.Value != 0m).OrderBy(p => p.Key).Select(p => new PlannedShare(p.Key, p.Value))];
        if (whole != 0m) distribution.Add(new PlannedShare(null, whole));
        var missing = WithoutVolume(list);
        return new PlannedItem(item, list[0].UnitId!.Value, list.Sum(Volume), distribution, undistributed, missing)
        {
            HasVolume = missing < list.Count,
        };
    }

    public static IReadOnlyList<PlannedCost> Cost(
        IReadOnlyList<PlannedRow> rows, IReadOnlyList<PlannedRowMaterial> materials,
        IReadOnlyDictionary<Guid, RowShare> shares, Guid? section)
    {
        var byRow = materials.ToLookup(m => m.RowId);
        return
        [
            .. ByItem(rows).Select(g =>
            {
                var list = g.ToList();
                var total = list.Any(r => r.Total is null) ? (decimal?)null
                    : list.Sum(r => Scale(r.Total!.Value, shares[r.Id].Weight(section), Money));
                var costs = list
                    .SelectMany(r => byRow[r.Id].Where(m => m.NomenclatureId is not null)
                        .Select(m => (Material: m, Weight: shares[r.Id].Weight(section))))
                    .GroupBy(x => x.Material.NomenclatureId!.Value).OrderBy(x => x.Key)
                    .Select(x => new PlannedMaterialCost(x.Key, x.Any(m => m.Material.Amount is null) ? null
                        : x.Sum(m => Scale(m.Material.Amount!.Value, m.Weight, Money))))
                    .ToList();
                return new PlannedCost(g.Key, total, costs);
            }),
        ];
    }

    public static PlannedMaterials Materials(
        IReadOnlyList<PlannedRow> rows, IReadOnlyList<PlannedRowMaterial> materials,
        IReadOnlyDictionary<Guid, RowShare> shares, Guid? section)
    {
        var rowOf = rows.ToDictionary(r => r.Id);
        var lines = materials.Where(m => m.NomenclatureId is not null)
            .GroupBy(m => (Item: rowOf[m.RowId].PlanItemId, Nomenclature: m.NomenclatureId!.Value, m.Unit))
            .OrderBy(g => g.Key.Item is null).ThenBy(g => g.Key.Item).ThenBy(g => g.Key.Nomenclature).ThenBy(g => g.Key.Unit)
            .Select(g => new PlannedMaterial(g.Key.Item, g.Key.Nomenclature,
                g.Sum(m => Scale(m.Quantity, shares[m.RowId].Weight(section), Quantities)), g.Key.Unit))
            .ToList();
        return new PlannedMaterials(lines, materials.Count(m => m.NomenclatureId is null));
    }

    /// <summary>
    /// Норма позиции: чел.-ч строк на их объём — по строкам, у которых трудозатраты есть и объём больше нуля.
    /// Строка «100 м» с коэффициентом 100 уже несёт объём в метрах, и норма выходит на метр (ТЗ PLAN-6.1).
    /// </summary>
    public static IReadOnlyList<PlannedLabor> Labor(
        IReadOnlyList<PlannedRow> rows, IReadOnlyList<PlannedRowLabor> labor,
        IReadOnlyDictionary<Guid, RowShare> shares, Guid? section)
    {
        var hoursOf = labor.GroupBy(l => l.RowId).ToDictionary(g => g.Key, g => g.Sum(l => l.ManHours));
        return
        [
            .. ByPlanItem(rows).Select(g =>
                {
                    var normed = g.Where(r => hoursOf.ContainsKey(r.Id) && Volume(r) > 0m)
                        .Select(r => (Hours: hoursOf[r.Id], Volume: Volume(r), Weight: shares[r.Id].Weight(section)))
                        .ToList();
                    var volume = normed.Sum(x => x.Volume * x.Weight);
                    return new PlannedLabor(g.Key,
                        volume > 0m ? Round(normed.Sum(x => x.Hours * x.Weight) / volume, Quantities) : null);
                }),
        ];
    }

    public static IReadOnlyList<PlannedHint> Hint(IReadOnlyList<PlannedRow> rows, IReadOnlyList<PlannedRowMaterial> materials)
    {
        var byRow = materials.ToLookup(m => m.RowId);
        return
        [
            .. ByItem(rows).Select(g => new PlannedHint(g.Key,
                [.. g.SelectMany(r => byRow[r.Id]).Where(m => m.NomenclatureId is not null)
                    .Select(m => m.NomenclatureId!.Value).Distinct().Order()])),
        ];
    }

    /// <summary>Строки по позиции перечня; строки-материалы (без позиции) — последней группой.</summary>
    private static IEnumerable<IGrouping<Guid?, PlannedRow>> ByItem(IReadOnlyList<PlannedRow> rows) =>
        rows.GroupBy(r => r.PlanItemId).OrderBy(g => g.Key is null).ThenBy(g => g.Key);

    /// <summary>Строки-работы по позиции перечня — тем же порядком, что <see cref="ByItem" />, без строк-материалов.</summary>
    private static IEnumerable<IGrouping<Guid, PlannedRow>> ByPlanItem(IReadOnlyList<PlannedRow> rows) =>
        rows.Where(r => r.PlanItemId is not null).GroupBy(r => r.PlanItemId!.Value).OrderBy(g => g.Key);

    /// <summary>Объём строки для суммы; строки без объёма считает <see cref="WithoutVolume" />, а не прячет нулём.</summary>
    private static decimal Volume(PlannedRow row) => row.Volume ?? 0m;

    private static int WithoutVolume(IEnumerable<PlannedRow> rows) => rows.Count(r => r.Volume is null);

    /// <summary>Доля числа: вся строка — число как есть, без округления.</summary>
    private static decimal Scale(decimal value, decimal weight, int decimals) =>
        weight == 1m ? value : Round(value * weight, decimals);

    private static decimal Round(decimal value, int decimals) => Math.Round(value, decimals, MidpointRounding.ToEven);
}
