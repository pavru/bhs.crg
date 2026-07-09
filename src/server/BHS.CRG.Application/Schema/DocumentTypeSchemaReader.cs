using System.Text.Json;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Schema;

/// <summary>Поле эффективной схемы типа: ключ, тип (string/complex/array/doc-ref/doc-array/…), typeId (для составных/массивов/ссылок), заголовок.</summary>
public record SchemaFieldInfo(string Key, string Type, Guid? TypeId, string? Title = null);

/// <summary>Скалярное ли поле (пригодное для табличного распознавания/материализации из плоских колонок).</summary>
public static class SchemaFieldKinds
{
    private static readonly HashSet<string> NonScalar = ["complex", "array", "doc-ref", "doc-array", "file", "image"];
    public static bool IsScalar(string type) => !NonScalar.Contains(type);
}

/// <summary>
/// Backend-чтение эффективной схемы типа документа (с учётом наследования по ParentId) — аналог
/// frontend-функции resolveEffectiveFields. Нужен резолверу материализации (issue #19) для
/// определения кардинальности целевого поля и проверки совместимости типов по наследованию.
/// </summary>
public static class DocumentTypeSchemaReader
{
    /// <summary>
    /// Эффективные поля типа: base → derived; excludedFields исключают унаследованные;
    /// одноимённые поля наследника перекрывают унаследованные (порядок: сначала базовые).
    /// </summary>
    public static IReadOnlyList<SchemaFieldInfo> EffectiveFields(Guid typeId, IReadOnlyDictionary<Guid, DocumentType> byId)
    {
        var chain = new List<DocumentType>();
        var cur = byId.GetValueOrDefault(typeId);
        var guard = 0;
        while (cur is not null && guard++ < 32)
        {
            chain.Add(cur);
            cur = cur.ParentId is { } p ? byId.GetValueOrDefault(p) : null;
        }
        chain.Reverse(); // root first

        var acc = new Dictionary<string, SchemaFieldInfo>();
        var order = new List<string>();
        foreach (var t in chain)
        {
            var (fields, excluded) = ParseSchema(t.Schema);
            foreach (var ex in excluded)
                if (acc.Remove(ex)) order.Remove(ex);
            foreach (var f in fields)
            {
                if (!acc.ContainsKey(f.Key)) order.Add(f.Key);
                acc[f.Key] = f;
            }
        }
        return order.Select(k => acc[k]).ToList();
    }

    public static SchemaFieldInfo? Field(Guid typeId, string key, IReadOnlyDictionary<Guid, DocumentType> byId)
        => EffectiveFields(typeId, byId).FirstOrDefault(f => f.Key == key);

    /// <summary>true, если childId == ancestorId либо childId — потомок ancestorId по ParentId.</summary>
    public static bool IsSameOrDescendant(Guid childId, Guid ancestorId, IReadOnlyDictionary<Guid, DocumentType> byId)
    {
        var cur = childId;
        var guard = 0;
        while (guard++ < 32)
        {
            if (cur == ancestorId) return true;
            var t = byId.GetValueOrDefault(cur);
            if (t?.ParentId is not { } p) return false;
            cur = p;
        }
        return false;
    }

    /// <summary>Поле-массив (много сущностей): array / doc-array.</summary>
    public static bool IsMultiValued(string fieldType) => fieldType is "array" or "doc-array";

    /// <summary>Поле-одиночная составная сущность/ссылка: complex / doc-ref.</summary>
    public static bool IsSingleComposite(string fieldType) => fieldType is "complex" or "doc-ref";

    private static (List<SchemaFieldInfo> Fields, List<string> Excluded) ParseSchema(JsonDocument schema)
    {
        var fields = new List<SchemaFieldInfo>();
        var excluded = new List<string>();
        var root = schema.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return (fields, excluded);

        if (root.TryGetProperty("fields", out var fs) && fs.ValueKind == JsonValueKind.Array)
            foreach (var f in fs.EnumerateArray())
            {
                if (f.ValueKind != JsonValueKind.Object) continue;
                var key = f.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
                if (string.IsNullOrEmpty(key)) continue;
                var type = f.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String ? ty.GetString()! : "string";
                Guid? typeId = f.TryGetProperty("typeId", out var ti) && ti.ValueKind == JsonValueKind.String
                    && Guid.TryParse(ti.GetString(), out var g) ? g : null;
                var title = f.TryGetProperty("title", out var tl) && tl.ValueKind == JsonValueKind.String ? tl.GetString() : null;
                fields.Add(new SchemaFieldInfo(key, type, typeId, title));
            }

        if (root.TryGetProperty("excludedFields", out var ex) && ex.ValueKind == JsonValueKind.Array)
            foreach (var e in ex.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String) excluded.Add(e.GetString()!);

        return (fields, excluded);
    }
}
