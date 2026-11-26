namespace BHS.CRG.Modules.Plan.Data;

/// <summary>
/// Строки плана стройки — единственный путь, которым строки сметы читаются для СУММ (change
/// <c>plan-06-estimate-model</c>, ТЗ PLAN-5.2, PLAN-4, D3 зонтика <c>plan-00-epic</c>).
///
/// <para><b>План стройки — сумма действующих редакций всех её смет.</b> Не всех редакций: прежняя
/// редакция и новая вместе удвоили бы объём. И не одной сметы: дополнительная смета — часть плана.
/// Несопоставленные строки (нет вида работы или позиции перечня) в план не входят: им не к чему
/// прибавиться ни в «Ходе работ», ни в графике (ТЗ PLAN-4). Строки-материалы — тоже (D2 <c>plan-31</c>).</para>
///
/// <para>⚠️ Ответ — запрос, а не загруженные строки: итоги считает база (<c>SumAsync</c>,
/// <c>GroupBy</c>), а строк на крупной стройке сотни тысяч. Потребители — плановые данные для других
/// модулей (<c>plan-23</c>) и «Ход работ» (<c>plan-25</c>).</para>
///
/// <para>⚠️ Прямое чтение <c>EstimateRows</c> для сумм мимо этого метода ловит
/// <c>PlanRowsInventoryTests</c>: перепись по исходникам знает каждое место, где строки сметы
/// упоминаются, и зачем.</para>
/// </summary>
public static class PlanRows
{
    /// <summary>Сопоставленные строки действующих редакций всех смет стройки.</summary>
    public static IQueryable<EstimateRow> Current(PlanDbContext db, Guid constructionId) =>
        from row in db.EstimateRows
        join estimate in db.Estimates on (Guid?)row.RevisionId equals estimate.CurrentRevisionId
        where estimate.ConstructionId == constructionId
              && row.Kind == EstimateRowKind.Work && row.WorkTypeId != null && row.PlanItemId != null
        select row;

    /// <summary>
    /// Несопоставленные строки-работы действующих редакций всех смет стройки — то, что в план НЕ вошло
    /// (ТЗ PLAN-4). Их число отдают «Плановые работы» (<c>plan-23</c>), чтобы план без них не читался как
    /// полный. Правило несопоставленной — одно, <see cref="EstimateRowCounts.Unmatched" />.
    /// </summary>
    public static IQueryable<EstimateRow> Unmatched(PlanDbContext db, Guid constructionId) =>
        from row in db.EstimateRows.Where(EstimateRowCounts.Unmatched)
        join estimate in db.Estimates on (Guid?)row.RevisionId equals estimate.CurrentRevisionId
        where estimate.ConstructionId == constructionId
        select row;

    internal sealed record UnmatchedRevision(Guid EstimateId, string EstimateName, Guid RevisionId, int Number, int Count);

    /// <summary>Источники счётчика несопоставленных строк для перехода к конкретным редакциям.</summary>
    internal static IQueryable<UnmatchedRevision> UnmatchedByRevision(PlanDbContext db, Guid constructionId)
    {
        var counts = Unmatched(db, constructionId).GroupBy(row => row.RevisionId)
            .Select(group => new { RevisionId = group.Key, Count = group.Count() });
        return from count in counts
               join revision in db.EstimateRevisions on count.RevisionId equals revision.Id
               join estimate in db.Estimates on revision.EstimateId equals estimate.Id
               orderby estimate.Name, estimate.Id
               select new UnmatchedRevision(estimate.Id, estimate.Name, revision.Id, revision.Number, count.Count);
    }

    /// <summary>
    /// Строки-материалы действующих редакций всех смет стройки (D2 <c>plan-31</c>): в план объёмов они не входят
    /// и позиции перечня не имеют, но их материалы и стоимость — часть сметы стройки. Их отдают «Плановые
    /// работы» (<c>plan-23</c>) без позиции перечня: без них заявка «из сметы» теряла бы большую часть
    /// материалов.
    /// </summary>
    public static IQueryable<EstimateRow> CurrentMaterialRows(PlanDbContext db, Guid constructionId) =>
        from row in db.EstimateRows
        join estimate in db.Estimates on (Guid?)row.RevisionId equals estimate.CurrentRevisionId
        where estimate.ConstructionId == constructionId && row.Kind == EstimateRowKind.Material
        select row;
}
