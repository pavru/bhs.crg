using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Documents;

public class Construction : Entity
{
    public string Name { get; private set; } = default!;
    public Guid CreatedByUserId { get; private set; }

    private readonly List<Section> _sections = [];
    public IReadOnlyList<Section> Sections => _sections.AsReadOnly();

    private Construction() { }

    public static Construction Create(string name, Guid userId)
        => new() { Name = name, CreatedByUserId = userId };

    public void Rename(string name) { Name = name; TouchUpdatedAt(); }
}
