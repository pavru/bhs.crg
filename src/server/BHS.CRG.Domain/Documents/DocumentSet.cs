using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Documents;

public class DocumentSet : Entity
{
    public string Name { get; private set; } = default!;
    public Guid SectionId { get; private set; }

    private readonly List<DocumentInstance> _instances = [];
    public IReadOnlyList<DocumentInstance> Instances => _instances.AsReadOnly();

    private DocumentSet() { }

    public static DocumentSet Create(Guid sectionId, string name)
        => new() { SectionId = sectionId, Name = name };

    public void Rename(string name) { Name = name; TouchUpdatedAt(); }
}
