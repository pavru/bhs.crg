using System.Text.Json;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Schema;

/// <summary>
/// Единый аксессор функциональных тэгов: hard-coded функционал находит поля/типы
/// пользовательской схемы по тэгам (а не по именам).
///
/// Формат в схеме: поле — <c>fields[].tags: string[]</c>; тип — <c>tags: string[]</c>.
/// </summary>
public static class SchemaTags
{
    /// <summary>
    /// (fieldKey, tag) по всей цепочке наследования типа (ближний тип имеет приоритет по ключу).
    /// Возвращает по одной паре на каждый тег поля.
    /// </summary>
    public static List<(string Key, string Tag)> TaggedFields(DocumentType docType, IReadOnlyList<DocumentType> allDocTypes)
    {
        var seenKeys = new HashSet<string>();
        var result = new List<(string Key, string Tag)>();
        var visited = new HashSet<Guid>();

        var current = docType;
        while (current is not null && visited.Add(current.Id))
        {
            foreach (var (key, tags) in FieldTags(current.Schema))
            {
                if (!seenKeys.Add(key)) continue; // ближний тип уже задал теги этого ключа
                foreach (var tag in tags)
                    result.Add((key, tag));
            }
            current = current.ParentId.HasValue
                ? allDocTypes.FirstOrDefault(dt => dt.Id == current.ParentId.Value)
                : null;
        }
        return result;
    }

    /// <summary>Ключи полей собственной схемы, несущих указанный тэг.</summary>
    public static IReadOnlyList<string> FieldKeysWithTag(JsonDocument schema, string tag)
    {
        var keys = new List<string>();
        foreach (var (key, tags) in FieldTags(schema))
            if (tags.Contains(tag))
                keys.Add(key);
        return keys;
    }

    /// <summary>Несёт ли сам тип (его собственная схема) тэг типа.</summary>
    public static bool SchemaHasTypeTag(JsonDocument schema, string tag)
    {
        if (schema.RootElement.ValueKind == JsonValueKind.Object
            && schema.RootElement.TryGetProperty("tags", out var tags)
            && tags.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tags.EnumerateArray())
                if (t.ValueKind == JsonValueKind.String && t.GetString() == tag) return true;
        }
        return false;
    }

    /// <summary>Несёт ли тип (или его предок) тэг типа.</summary>
    public static bool TypeHasTag(DocumentType docType, IReadOnlyList<DocumentType> allDocTypes, string tag)
    {
        var visited = new HashSet<Guid>();
        var current = docType;
        while (current is not null && visited.Add(current.Id))
        {
            if (SchemaHasTypeTag(current.Schema, tag)) return true;
            current = current.ParentId.HasValue
                ? allDocTypes.FirstOrDefault(dt => dt.Id == current.ParentId.Value)
                : null;
        }
        return false;
    }

    /// <summary>
    /// Накладывает метаданные на реквизиты: для каждого (key, tag) берёт meta[tag], если есть.
    /// </summary>
    public static JsonDocument PatchMetadata(
        JsonDocument current,
        List<(string Key, string Tag)> taggedFields,
        Dictionary<string, object?> meta)
    {
        var dict = new Dictionary<string, JsonElement>();
        foreach (var p in current.RootElement.EnumerateObject())
            dict[p.Name] = p.Value.Clone();

        foreach (var (key, tag) in taggedFields)
            if (meta.TryGetValue(tag, out var value))
                dict[key] = JsonSerializer.SerializeToElement(value);

        return JsonDocument.Parse(JsonSerializer.Serialize(dict));
    }

    // ── внутреннее: перечисление (fieldKey, tags[]) собственной схемы ────────────
    private static IEnumerable<(string Key, string[] Tags)> FieldTags(JsonDocument schema)
    {
        if (schema.RootElement.ValueKind != JsonValueKind.Object
            || !schema.RootElement.TryGetProperty("fields", out var fields)
            || fields.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var field in fields.EnumerateArray())
        {
            if (!field.TryGetProperty("key", out var keyProp)) continue;
            var key = keyProp.GetString();
            if (string.IsNullOrEmpty(key)) continue;
            if (!field.TryGetProperty("tags", out var tagsEl) || tagsEl.ValueKind != JsonValueKind.Array) continue;

            var tags = tagsEl.EnumerateArray()
                .Where(t => t.ValueKind == JsonValueKind.String)
                .Select(t => t.GetString()!)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToArray();
            if (tags.Length > 0) yield return (key, tags);
        }
    }
}
