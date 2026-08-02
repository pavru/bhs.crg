using System.Text.Json;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;

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
    ///
    /// Тэг отдаётся КОДОМ, без параметра (issue #583): запись «identity:2» приходит сюда как
    /// «identity». Так весь существующий функционал, сравнивающий тэг с константой, продолжает
    /// работать, не зная о параметрах вовсе, — иначе поле с номером молча выпало бы из
    /// сопоставления. За порядком идти в <see cref="OrderedKeysWithTag" />.
    /// </summary>
    public static List<(string Key, string Tag)> TaggedFields(DocumentType docType, IReadOnlyList<DocumentType> allDocTypes)
        => [.. TaggedFieldsWithOrder(docType, allDocTypes).Select(f => (f.Key, f.Tag.Code))];

    /// <summary>То же, но с параметром тэга — для функционала, которому важен порядок.</summary>
    public static List<(string Key, TagCode Tag)> TaggedFieldsWithOrder(
        DocumentType docType, IReadOnlyList<DocumentType> allDocTypes)
    {
        var seenKeys = new HashSet<string>();
        var result = new List<(string Key, TagCode Tag)>();
        var visited = new HashSet<Guid>();

        var current = docType;
        while (current is not null && visited.Add(current.Id))
        {
            foreach (var (key, tags) in FieldTags(current.Schema))
            {
                if (!seenKeys.Add(key)) continue; // ближний тип уже задал теги этого ключа
                foreach (var tag in tags)
                    result.Add((key, TagCode.Parse(tag)));
            }
            current = current.ParentId.HasValue
                ? allDocTypes.FirstOrDefault(dt => dt.Id == current.ParentId.Value)
                : null;
        }
        return result;
    }

    /// <summary>
    /// То же, что <see cref="TaggedFieldsWithOrder" />, но в ЭФФЕКТИВНОМ порядке схемы: поля предка
    /// идут перед собственными, наследованное поле стоит там, где объявлено, а исключённое
    /// (<c>excludedFields</c>) не участвует вовсе. Ровно так их показывает форма — клиентский
    /// <c>resolveEffectiveFields</c>.
    ///
    /// Обход вверх (ближний тип первым) нужен, чтобы тэги ближнего типа побеждали, и ни порядком, ни
    /// составом быть не может: порядок он даёт обратный, а исключения не видит вовсе. И то и другое
    /// видно пользователю и задаёт составной ключ (#583) — разойдись с клиентским, ключ связки,
    /// заведённой в UI, не совпал бы с ключом на генерации, и связка молча не срабатывала бы.
    /// </summary>
    public static List<(string Key, TagCode Tag)> TaggedFieldsInSchemaOrder(
        DocumentType docType, IReadOnlyList<DocumentType> allDocTypes)
    {
        var byKey = new Dictionary<string, List<TagCode>>();
        foreach (var (key, tag) in TaggedFieldsWithOrder(docType, allDocTypes))
        {
            if (!byKey.TryGetValue(key, out var tags)) byKey[key] = tags = [];
            tags.Add(tag);
        }

        // Цепочка от КОРНЯ к листу: место поля задаёт тип, который его объявил, а не тот, что
        // переопределил тэги.
        var chain = new List<DocumentType>();
        var visited = new HashSet<Guid>();
        var current = docType;
        while (current is not null && visited.Add(current.Id))
        {
            chain.Add(current);
            current = current.ParentId.HasValue
                ? allDocTypes.FirstOrDefault(dt => dt.Id == current.ParentId.Value)
                : null;
        }
        chain.Reverse();

        var placed = new List<string>();
        var seen = new HashSet<string>();
        foreach (var type in chain)
        {
            // Исключения применяем ДО собственных полей типа: excludedFields относятся к
            // унаследованному, и порядок здесь тот же, что у DocumentTypeSchemaReader.EffectiveFields.
            foreach (var excluded in ExcludedFields(type.Schema))
                if (seen.Remove(excluded)) placed.Remove(excluded);

            foreach (var (key, _) in FieldTags(type.Schema))
                if (seen.Add(key)) placed.Add(key);
        }

        var result = new List<(string Key, TagCode Tag)>();
        foreach (var key in placed)
            if (byKey.TryGetValue(key, out var tags))
                result.AddRange(tags.Select(t => (key, t)));
        return result;
    }

    /// <summary>
    /// Ключи полей с указанным тэгом, упорядоченные ПАРАМЕТРОМ тэга (issue #583): «identity:1» перед
    /// «identity:2», поля без номера — следом, в порядке схемы.
    ///
    /// Порядок здесь не украшение: он задаёт порядок компонентов составного ключа идентичности, и
    /// его изменение меняет ключи всех объектов разом.
    /// </summary>
    public static IReadOnlyList<string> OrderedKeysWithTag(
        DocumentType docType, IReadOnlyList<DocumentType> allDocTypes, string tag)
        => [.. TaggedFieldsInSchemaOrder(docType, allDocTypes)
            .Select((f, index) => (f.Key, f.Tag, index))
            .Where(x => x.Tag.Code == tag)
            .OrderBy(x => x.Tag.SortKey)
            .ThenBy(x => x.index)          // стабильность: одинаковые номера не «прыгают» между вызовами
            .Select(x => x.Key)];

    /// <summary>Ключи полей собственной схемы, несущих указанный тэг.</summary>
    public static IReadOnlyList<string> FieldKeysWithTag(JsonDocument schema, string tag)
    {
        var keys = new List<string>();
        foreach (var (key, tags) in FieldTags(schema))
            if (tags.Any(t => TagCode.CodeOf(t) == tag))
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
                if (t.ValueKind == JsonValueKind.String && TagCode.CodeOf(t.GetString()) == tag) return true;
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

    // ── внутреннее: ключи полей, исключённых типом из наследованных ──────────────
    private static IEnumerable<string> ExcludedFields(JsonDocument schema)
    {
        if (schema.RootElement.ValueKind != JsonValueKind.Object
            || !schema.RootElement.TryGetProperty("excludedFields", out var excluded)
            || excluded.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var key in excluded.EnumerateArray())
            if (key.ValueKind == JsonValueKind.String && key.GetString() is { Length: > 0 } value)
                yield return value;
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
