using System.Text.Json;
using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Catalog;

/// <summary>
/// Аналог сущностей КаталогОбщихДанных старой системы:
/// Organization, Person, ConstructionObject, Project.
/// Тип определяется EntityType, данные хранятся в JSONB.
/// </summary>
public class CatalogEntity : Entity
{
    public string EntityType { get; private set; } = default!;
    public string DisplayName { get; private set; } = default!;
    public JsonDocument Data { get; private set; } = default!;
    public Guid? OwnerId { get; private set; }

    private CatalogEntity() { }

    public static CatalogEntity Create(string entityType, string displayName, JsonDocument data, Guid? ownerId = null)
        => new()
        {
            EntityType = entityType,
            DisplayName = displayName,
            Data = data,
            OwnerId = ownerId,
        };

    public static CatalogEntity Restore(
        Guid id, string entityType, string displayName, JsonDocument data,
        Guid? ownerId, DateTimeOffset createdAt, DateTimeOffset updatedAt)
        => new()
        {
            Id = id, EntityType = entityType, DisplayName = displayName,
            Data = data, OwnerId = ownerId, CreatedAt = createdAt, UpdatedAt = updatedAt,
        };

    public void Update(string displayName, JsonDocument data)
    {
        DisplayName = displayName;
        Data = data;
        TouchUpdatedAt();
    }
}
