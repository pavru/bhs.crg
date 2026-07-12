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

    /// <summary>Альтернативные имена (issue #74) — используются при сопоставлении записи со значением
    /// колонки источника данных наравне с <see cref="DisplayName"/> (когда матч идёт по имени).</summary>
    public List<string> Aliases { get; private set; } = [];

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
        CatalogScope scope, Guid? scopeId, IReadOnlyList<string>? aliases = null)
        => new()
        {
            DisplayName = displayName,
            Aliases = NormalizeAliases(aliases),
            CompositeTypeId = compositeTypeId,
            Data = data,
            Scope = scope,
            ScopeId = scopeId,
        };

    public static CommonDataEntry Restore(
        Guid id, string displayName, Guid compositeTypeId, JsonDocument data,
        CatalogScope scope, Guid? scopeId, DateTimeOffset createdAt, DateTimeOffset updatedAt,
        IReadOnlyList<string>? aliases = null)
        => new()
        {
            Id = id, DisplayName = displayName, Aliases = NormalizeAliases(aliases),
            CompositeTypeId = compositeTypeId,
            Data = data, Scope = scope, ScopeId = scopeId,
            CreatedAt = createdAt, UpdatedAt = updatedAt,
        };

    public void Update(string displayName, JsonDocument data, IReadOnlyList<string>? aliases = null)
    {
        DisplayName = displayName;
        Aliases = NormalizeAliases(aliases);
        Data = data;
        TouchUpdatedAt();
    }

    /// Убираем пустые/дублирующиеся алиасы (без учёта регистра), сохраняя порядок.
    private static List<string> NormalizeAliases(IReadOnlyList<string>? aliases)
    {
        if (aliases is null) return [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var a in aliases)
        {
            var t = a?.Trim();
            if (!string.IsNullOrEmpty(t) && seen.Add(t)) result.Add(t);
        }
        return result;
    }
}
