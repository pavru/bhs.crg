namespace BHS.CRG.Modules.Costs.Data;

/// <summary>Переход хранит причину до подтверждения записи в журнале, в том числе после удаления заявки.</summary>
internal sealed class PurchaseRequestReturnEvent
{
    private PurchaseRequestReturnEvent() { }

    public Guid EventId { get; private set; }
    public Guid RequestId { get; private set; }
    public long RequestNumber { get; private set; }
    public Guid AuthorId { get; private set; }
    public string AuthorName { get; private set; } = null!;
    public DateTimeOffset OccurredAt { get; private set; }
    public string Reason { get; private set; } = null!;

    public static PurchaseRequestReturnEvent Create(
        PurchaseRequest request, Guid authorId, string authorName, DateTimeOffset now, string reason) => new()
    {
        EventId = Guid.CreateVersion7(),
        RequestId = request.Id,
        RequestNumber = request.Number,
        AuthorId = authorId,
        AuthorName = authorName,
        OccurredAt = now,
        Reason = reason,
    };
}
