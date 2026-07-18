using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Documents;

/// <summary>
/// Комплект документов. Документы комплекта — это <c>DomainObject</c> на оси (Set, этот Id)
/// (issue #84): прямой навигации нет, они запрашиваются по расположению.
/// </summary>
public class DocumentSet : Entity
{
    public string Name { get; private set; } = default!;
    public Guid SectionId { get; private set; }

    /// <summary>Объект-профиль уровня (issue #258) — DomainObject профиль-типа на scope комплекта, если создан.</summary>
    public Guid? ProfileObjectId { get; private set; }
    public void SetProfileObject(Guid objectId) { ProfileObjectId = objectId; TouchUpdatedAt(); }

    private DocumentSet() { }

    public static DocumentSet Create(Guid sectionId, string name)
        => new() { SectionId = sectionId, Name = name };

    public void Rename(string name) { Name = name; TouchUpdatedAt(); }
}
