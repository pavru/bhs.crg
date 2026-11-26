namespace BHS.CRG.Modules.Costs.Data;

public enum PurchaseRequestStatus { Draft, Submitted, Closed }

public sealed class PurchaseRequest
{
    private readonly List<PurchaseRequestLine> _lines = [];

    private PurchaseRequest() { }

    public Guid Id { get; private set; }
    public long Number { get; private set; }
    public Guid ConstructionId { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public PurchaseRequestStatus Status { get; private set; }
    public IReadOnlyList<PurchaseRequestLine> Lines => _lines;

    public static PurchaseRequest Create(Guid constructionId, Guid author, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ConstructionId = constructionId,
            CreatedBy = author,
            CreatedAt = now,
            UpdatedAt = now,
            Status = PurchaseRequestStatus.Draft,
        };

    internal void ReplaceLines(IReadOnlyList<PurchaseRequestLineInput> lines, DateTimeOffset now)
    {
        _lines.Clear();
        for (var i = 0; i < lines.Count; i++)
            _lines.Add(new PurchaseRequestLine(Id, i + 1, lines[i]));
        UpdatedAt = now;
    }

    internal void ChangeStatus(PurchaseRequestStatus status, DateTimeOffset now)
    {
        Status = status;
        UpdatedAt = now;
    }
}
