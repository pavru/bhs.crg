using System.Text.Json;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Generation;

public static class DocumentMetaTagHelper
{
    /// <summary>
    /// Возвращает список (fieldKey, metaTag) для всей цепочки наследования типа документа.
    /// Тег ближайшего типа в цепочке имеет приоритет.
    /// </summary>
    public static List<(string Key, string Tag)> GetTaggedFields(
        DocumentType docType,
        IReadOnlyList<DocumentType> allDocTypes)
    {
        var result = new Dictionary<string, string>();
        var visited = new HashSet<Guid>();

        var current = docType;
        while (current is not null && visited.Add(current.Id))
        {
            if (current.Schema.RootElement.TryGetProperty("fields", out var fields)
                && fields.ValueKind == JsonValueKind.Array)
            {
                foreach (var field in fields.EnumerateArray())
                {
                    if (!field.TryGetProperty("key", out var keyProp)) continue;
                    if (!field.TryGetProperty("metaTag", out var tagProp)) continue;
                    var key = keyProp.GetString();
                    var tag = tagProp.GetString();
                    if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(tag) && !result.ContainsKey(key))
                        result[key] = tag;
                }
            }
            current = current.ParentId.HasValue
                ? allDocTypes.FirstOrDefault(dt => dt.Id == current.ParentId.Value)
                : null;
        }

        return result.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>
    /// Накладывает метаданные на реквизиты: возвращает новый JsonDocument
    /// с обновлёнными полями согласно taggedFields + словарю meta.
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
        {
            if (meta.TryGetValue(tag, out var value))
                dict[key] = JsonSerializer.SerializeToElement(value);
        }

        return JsonDocument.Parse(JsonSerializer.Serialize(dict));
    }
}
