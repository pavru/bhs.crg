namespace BHS.CRG.Modules.Plan.Data;

/// <summary>
/// Часть строки редакции на разделе стройки (change <c>plan-10-distribution</c>, ТЗ PLAN-5.1): «строка → раздел →
/// количество». Исключение из правила раздела ЛСР: строка с частями правилу не подчиняется, остаток — «стройка в
/// целом» (баланс как у разноски, ТЗ COST-13).
///
/// <para><b>Количество — в единице строки</b>, как в файле (<see cref="EstimateRow.SourceVolume" />), с шестью
/// знаками: части, присланные в единице плана, переводятся делением на коэффициент
/// (<c>DistributionMath.Store</c>). Сумма частей строки не больше её объёма — это держат все пути записи: правка,
/// перенос при повторном импорте и решение по отчёту переноса.</para>
/// </summary>
public sealed class DistributionPart
{
    private DistributionPart() { }

    public Guid Id { get; private set; }

    public Guid RevisionId { get; private set; }

    public Guid RowId { get; private set; }

    /// <summary>Раздел стройки — запись ядра, идентификатором без внешнего ключа (ТЗ CORE-34.1).</summary>
    public Guid SectionId { get; private set; }

    /// <summary>Количество в единице строки, шесть знаков.</summary>
    public decimal Quantity { get; private set; }

    internal static DistributionPart Create(Guid revisionId, Guid rowId, Guid sectionId, decimal quantity) =>
        new()
        {
            Id = Guid.NewGuid(),
            RevisionId = revisionId,
            RowId = rowId,
            SectionId = sectionId,
            Quantity = quantity,
        };
}
