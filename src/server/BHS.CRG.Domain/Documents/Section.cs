using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Documents;

public class Section : Entity
{
    public string Name { get; private set; } = default!;
    public Guid ConstructionId { get; private set; }

    /// <summary>Объект-профиль уровня (issue #258) — DomainObject профиль-типа на scope раздела, если создан.</summary>
    public Guid? ProfileObjectId { get; private set; }
    public void SetProfileObject(Guid objectId) { ProfileObjectId = objectId; TouchUpdatedAt(); }

    private readonly List<DocumentSet> _documentSets = [];
    public IReadOnlyList<DocumentSet> DocumentSets => _documentSets.AsReadOnly();

    private Section() { }

    public static Section Create(Guid constructionId, string name)
        => new() { ConstructionId = constructionId, Name = name };

    public void Rename(string name) { Name = name; TouchUpdatedAt(); }
}
