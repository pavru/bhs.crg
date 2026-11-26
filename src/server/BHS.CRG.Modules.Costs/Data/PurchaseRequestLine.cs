namespace BHS.CRG.Modules.Costs.Data;

public sealed class PurchaseRequestLine
{
    internal const int UnitLength = InvoiceLine.UnitLength;
    internal const int NoteLength = 2000;
    // Плановые работы возвращают доли материалов с шестью знаками после запятой.
    internal const int QuantityScale = 6;
    internal const decimal MaxQuantity = 99_999_999_999_999.999999m;

    private PurchaseRequestLine() { }

    internal PurchaseRequestLine(Guid requestId, int position, PurchaseRequestLineInput input)
    {
        Id = Guid.CreateVersion7();
        RequestId = requestId;
        Position = position;
        NomenclatureId = input.NomenclatureId;
        PlanItemId = input.PlanItemId;
        Quantity = input.Quantity;
        Unit = input.Unit;
        Note = input.Note;
    }

    public Guid Id { get; private set; }
    public Guid RequestId { get; private set; }
    public int Position { get; private set; }
    public Guid NomenclatureId { get; private set; }
    public Guid? PlanItemId { get; private set; }
    public decimal Quantity { get; private set; }
    public string Unit { get; private set; } = null!;
    public string? Note { get; private set; }
}

internal sealed record PurchaseRequestLineInput(
    Guid NomenclatureId, Guid? PlanItemId, decimal Quantity, string Unit, string? Note);
