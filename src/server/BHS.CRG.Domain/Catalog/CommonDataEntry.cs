using System.Text.Json;
using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Catalog;

/// <summary>
/// Приоритет — значение enum напрямую:
/// Set=1 (высший), Section=2, Construction=3, System=5 (низший).
/// </summary>
public enum CatalogScope { Set = 1, Section = 2, Construction = 3, System = 5 }

/// <summary>
/// Именованный экземпляр составного типа в иерархическом каталоге общих данных.
/// </summary>
public class CommonDataEntry : Entity
{
    public string DisplayName { get; private set; } = null!;

    /// <summary>ID составного типа документа (DocumentType.Kind == Composite).</summary>
    public Guid CompositeTypeId { get; private set; }

    /// <summary>Данные полей экземпляра.</summary>
    public JsonDocument Data { get; private set; } = null!;

    public CatalogScope Scope { get; private set; }

    /// <summary>null для System-скоупа.</summary>
    public Guid? ScopeId { get; private set; }

    private CommonDataEntry() { }

    public static CommonDataEntry Create(
        string displayName, Guid compositeTypeId, JsonDocument data,
        CatalogScope scope, Guid? scopeId)
        => new()
        {
            DisplayName = displayName,
            CompositeTypeId = compositeTypeId,
            Data = data,
            Scope = scope,
            ScopeId = scopeId,
        };

    public static CommonDataEntry Restore(
        Guid id, string displayName, Guid compositeTypeId, JsonDocument data,
        CatalogScope scope, Guid? scopeId, DateTimeOffset createdAt, DateTimeOffset updatedAt)
        => new()
        {
            Id = id, DisplayName = displayName, CompositeTypeId = compositeTypeId,
            Data = data, Scope = scope, ScopeId = scopeId,
            CreatedAt = createdAt, UpdatedAt = updatedAt,
        };

    public void Update(string displayName, JsonDocument data)
    {
        DisplayName = displayName;
        Data = data;
        TouchUpdatedAt();
    }
}
