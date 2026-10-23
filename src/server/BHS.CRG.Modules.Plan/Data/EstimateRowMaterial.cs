namespace BHS.CRG.Modules.Plan.Data;

/// <summary>
/// Материал позиции сметы (ТЗ PLAN-9.1): позиция номенклатуры ядра — или пусто, пока не сопоставлен, —
/// наименование, код и единица из файла, количество на объём строки и сметная стоимость ресурса, если
/// файл её несёт. Цена и стоимость — под <c>plan.estimate.cost</c>, как стоимость строки (D19 зонтика):
/// их читают «план против факта» материалов (<c>plan-27</c>) и денежная часть плана (<c>plan-23</c>).
/// </summary>
public sealed class EstimateRowMaterial
{
    private EstimateRowMaterial() { }

    public Guid Id { get; private set; }

    public Guid RowId { get; private set; }

    /// <summary>Порядок ресурса в строке, как в файле.</summary>
    public int Order { get; private set; }

    public Guid? NomenclatureId { get; private set; }

    public string SourceName { get; private set; } = string.Empty;

    public string? SourceCode { get; private set; }

    public string? SourceUnit { get; private set; }

    /// <summary>Количество на объём строки — в единице из файла.</summary>
    public decimal Quantity { get; private set; }

    public decimal? UnitPrice { get; private set; }

    public decimal? Amount { get; private set; }

    public bool Unmatched => NomenclatureId is null;

    internal static EstimateRowMaterial Create(Guid rowId, int order, EstimateMaterialDraft draft) => new()
    {
        Id = Guid.NewGuid(),
        RowId = rowId,
        Order = order,
        NomenclatureId = draft.NomenclatureId,
        SourceName = draft.SourceName,
        SourceCode = Clip(draft.SourceCode, EstimateRow.RateCodeLength),
        SourceUnit = Clip(draft.SourceUnit, EstimateRow.UnitLength),
        Quantity = draft.Quantity,
        UnitPrice = draft.UnitPrice,
        Amount = draft.Amount,
    };

    private static string? Clip(string? value, int length) =>
        value is null ? null : value.Length <= length ? value : value[..length];
}
