using System.Linq.Expressions;

namespace BHS.CRG.Modules.Plan.Data;

/// <summary>
/// Счётчики строк редакции для показа — одним правилом для редакции, раздела ЛСР и предпросмотра смены
/// действующей (change <c>plan-31-estimate-ui-carriers</c>, D2): «не сопоставлена» — только строка-работа без вида
/// работы или позиции перечня, как <see cref="EstimateRow.Unmatched" />, но запросом, который считает база.
///
/// <para>⚠️ Правило записано здесь дважды — условием отбора (<see cref="Unmatched" />) и внутри группировки
/// (<see cref="By{TKey}" />): в счёт по группе выражение-переменную не передать. Меняя одно, меняйте и другое —
/// и <see cref="EstimateRow.Unmatched" />.</para>
///
/// <para>⚠️ Итоги ОДНОЙ редакции для показа, а не план стройки — тот только <see cref="PlanRows.Current" />.</para>
/// </summary>
public static class EstimateRowCounts
{
    /// <summary>Несопоставленная строка-работа — условием отбора.</summary>
    public static readonly Expression<Func<EstimateRow, bool>> Unmatched =
        r => r.Kind == EstimateRowKind.Work && (r.WorkTypeId == null || r.PlanItemId == null);

    /// <summary>Строки по ключу: всего, не сопоставлено, первая по порядку файла и — с <paramref name="total" /> — итог.</summary>
    /// <param name="total">Считать итог: без права на себестоимость сумма не считается вовсе.</param>
    public static IQueryable<EstimateRowCount<TKey>> By<TKey>(
        IQueryable<EstimateRow> rows, Expression<Func<EstimateRow, TKey>> key, bool total) =>
        total
            ? rows.GroupBy(key).Select(g => new EstimateRowCount<TKey>(
                g.Key, g.Count(),
                g.Count(r => r.Kind == EstimateRowKind.Work && (r.WorkTypeId == null || r.PlanItemId == null)),
                g.Min(r => r.Order), g.Sum(r => r.Total ?? 0m)))
            : rows.GroupBy(key).Select(g => new EstimateRowCount<TKey>(
                g.Key, g.Count(),
                g.Count(r => r.Kind == EstimateRowKind.Work && (r.WorkTypeId == null || r.PlanItemId == null)),
                g.Min(r => r.Order), null));
}

/// <param name="First">Порядок первой строки группы — по нему группы идут в порядке файла.</param>
/// <param name="Total">Итог по строкам; <c>null</c> — не считался: у читающего нет права.</param>
public sealed record EstimateRowCount<TKey>(TKey Key, int Rows, int Unmatched, int First, decimal? Total);
