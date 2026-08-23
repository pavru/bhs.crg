using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Documents;

public class Construction : Entity
{
    public string Name { get; private set; } = default!;
    public Guid CreatedByUserId { get; private set; }

    /// <summary>Объект-профиль уровня (issue #258) — DomainObject профиль-типа на scope стройки, если создан.
    /// Простой nullable-указатель (не DB-FK: объект — часть агрегата контейнера, каскад по scope).</summary>
    public Guid? ProfileObjectId { get; private set; }
    public void SetProfileObject(Guid objectId) { ProfileObjectId = objectId; TouchUpdatedAt(); }

    private readonly List<Section> _sections = [];
    public IReadOnlyList<Section> Sections => _sections.AsReadOnly();

    private Construction() { }

    public static Construction Create(string name, Guid userId)
        => new() { Name = name, CreatedByUserId = userId };

    /// <summary>Восстановление из резервной копии (issue #833): идентификатор и время — как были.</summary>
    public static Construction Restore(Guid id, string name, Guid createdByUserId, Guid? profileObjectId,
        DateTimeOffset createdAt, DateTimeOffset updatedAt)
        => new()
        {
            Id = id, Name = name, CreatedByUserId = createdByUserId, ProfileObjectId = profileObjectId,
            CreatedAt = createdAt, UpdatedAt = updatedAt,
        };

    public void Rename(string name) { Name = name; TouchUpdatedAt(); }
}
