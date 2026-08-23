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

    /// <summary>Восстановление из резервной копии (issue #833).</summary>
    public static Section Restore(Guid id, Guid constructionId, string name, Guid? profileObjectId,
        DateTimeOffset createdAt, DateTimeOffset updatedAt)
        => new()
        {
            Id = id, ConstructionId = constructionId, Name = name, ProfileObjectId = profileObjectId,
            CreatedAt = createdAt, UpdatedAt = updatedAt,
        };

    public void Rename(string name) { Name = name; TouchUpdatedAt(); }
}
