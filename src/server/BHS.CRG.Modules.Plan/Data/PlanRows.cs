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
}
