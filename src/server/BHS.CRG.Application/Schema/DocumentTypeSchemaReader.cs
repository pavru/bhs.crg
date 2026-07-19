using System.Text.Json;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Schema;

/// <summary>Один вариант перечисления: код (хранится в реквизитах) + отображаемое имя.</summary>
public record EnumOptionInfo(string Code, string Label);

/// <summary>Поле эффективной схемы типа: ключ, тип (string/complex/array/doc-ref/doc-array/…), typeId (для составных/массивов/ссылок), заголовок.</summary>
/// <param name="DefaultValue">Значение по умолчанию (issue #53) — из собственного объявления поля, либо
/// переопределено производным типом через fieldOverrides (для унаследованных полей). Null — не задано.
/// Применяется резолвером генерации, только если поле НЕ определено ни в реквизитах инстанса, ни через
/// привязку набора данных (см. EntityResolver.ApplyDefaultsAsync).</param>
/// <param name="Options">Варианты для type="enum" (issue #59) — из собственного инлайн `options`
/// (легаси: код==имя) либо резолвлены из EnumType по `typeId`. Null — не enum-поле, либо enumTypesById
/// не передан вызывающим кодом.</param>
public record SchemaFieldInfo(string Key, string Type, Guid? TypeId, string? Title = null,
    JsonElement? DefaultValue = null, IReadOnlyList<EnumOptionInfo>? Options = null);

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
    public static IReadOnlyList<SchemaFieldInfo> EffectiveFields(Guid typeId, IReadOnlyDictionary<Guid, DocumentType> byId,
        IReadOnlyDictionary<Guid, EnumType>? enumTypesById = null)
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
            var (fields, excluded, overrides) = ParseSchema(t.Schema, enumTypesById);
            foreach (var ex in excluded)
                if (acc.Remove(ex)) order.Remove(ex);

            // Переопределение defaultValue унаследованного поля (issue #53, fieldOverrides) — ДО добавления
            // собственных полей этого типа: переопределения относятся к полям, унаследованным от предков,
            // не к собственным полям типа (те несут свой defaultValue напрямую в объявлении).
            foreach (var (key, dv) in overrides)
                if (acc.TryGetValue(key, out var existing))
                    acc[key] = existing with { DefaultValue = dv };

            foreach (var f in fields)
            {
                if (!acc.ContainsKey(f.Key)) order.Add(f.Key);
                acc[f.Key] = f;
            }
        }
        return order.Select(k => acc[k]).ToList();
    }

    public static SchemaFieldInfo? Field(Guid typeId, string key, IReadOnlyDictionary<Guid, DocumentType> byId,
        IReadOnlyDictionary<Guid, EnumType>? enumTypesById = null)
        => EffectiveFields(typeId, byId, enumTypesById).FirstOrDefault(f => f.Key == key);

    /// <summary>
    /// Ссылается ли схема (СОБСТВЕННЫЕ поля типа — без резолва наследования) на составной/ссылочный
    /// тип documentTypeId через complex/array/doc-ref/doc-array поле (issue #57, п.7 — проверка перед
    /// удалением типа документа). Наследование резолвить не нужно: ссылка typeId хранится там, где
    /// поле было изначально объявлено, а не синтезируется заново для каждого потомка.
    /// </summary>
    public static bool ReferencesType(JsonDocument schema, Guid documentTypeId)
    {
        var (fields, _, _) = ParseSchema(schema, null);
        return fields.Any(f => f.TypeId == documentTypeId && (IsSingleComposite(f.Type) || IsMultiValued(f.Type)));
    }

    /// <summary>Ссылается ли схема (СОБСТВЕННЫЕ поля типа) на тип перечисления enumTypeId через
    /// поле type="enum" + typeId (issue #59, проверка перед удалением EnumType).</summary>
    public static bool ReferencesEnumType(JsonDocument schema, Guid enumTypeId)
    {
        var (fields, _, _) = ParseSchema(schema, null);
        return fields.Any(f => f.Type == "enum" && f.TypeId == enumTypeId);
    }

    /// <summary>Ссылается ли схема (СОБСТВЕННЫЕ поля типа) на тип поля из реестра primitiveTypeId
    /// через поле type="primitive" + typeId (issue #269, проверка перед удалением PrimitiveType —
    /// по образцу <see cref="ReferencesEnumType"/>).</summary>
    public static bool ReferencesPrimitiveType(JsonDocument schema, Guid primitiveTypeId)
    {
        var (fields, _, _) = ParseSchema(schema, null);
        return fields.Any(f => f.Type == "primitive" && f.TypeId == primitiveTypeId);
    }

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

    private static (List<SchemaFieldInfo> Fields, List<string> Excluded, Dictionary<string, JsonElement> Overrides) ParseSchema(
        JsonDocument schema, IReadOnlyDictionary<Guid, EnumType>? enumTypesById)
    {
        var fields = new List<SchemaFieldInfo>();
        var excluded = new List<string>();
        var overrides = new Dictionary<string, JsonElement>();
        var root = schema.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return (fields, excluded, overrides);

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
                // defaultValue (issue #53) — Clone(), т.к. JsonElement иначе привязан к времени жизни
                // ЭТОГО JsonDocument (schema — параметр метода, может быть освобождён вызывающим).
                JsonElement? defaultValue = f.TryGetProperty("defaultValue", out var dv) && dv.ValueKind != JsonValueKind.Undefined
                    ? dv.Clone() : null;
                // Enum-варианты (issue #59): легаси инлайн `options` (код==имя, старое поведение
                // 1:1) — если пусто, резолвим typeId через EnumType (толерантное сосуществование
                // обоих представлений, без принудительной миграции).
                IReadOnlyList<EnumOptionInfo>? options = type == "enum" ? ParseEnumOptions(f, typeId, enumTypesById) : null;
                fields.Add(new SchemaFieldInfo(key, type, typeId, title, defaultValue, options));
            }

        if (root.TryGetProperty("excludedFields", out var ex) && ex.ValueKind == JsonValueKind.Array)
            foreach (var e in ex.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String) excluded.Add(e.GetString()!);

        // fieldOverrides (issue #53) — переопределение defaultValue УНАСЛЕДОВАННОГО поля производным
        // типом (см. resolveEffectiveFields на фронте — тот же паттерн). required-override здесь
        // намеренно не читаем — это отдельная (не запрошенная) забота валидации, не резолва значений.
        if (root.TryGetProperty("fieldOverrides", out var ovs) && ovs.ValueKind == JsonValueKind.Object)
            foreach (var prop in ovs.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.Object
                    && prop.Value.TryGetProperty("defaultValue", out var odv) && odv.ValueKind != JsonValueKind.Undefined)
                    overrides[prop.Name] = odv.Clone();

        return (fields, excluded, overrides);
    }

    // Легаси инлайн options (issue #59): каждая строка — и код, и отображаемое имя (как ведёт себя
    // сегодняшнее поведение enum-поля 1:1). Если options пуст/отсутствует — резолв через typeId.
    private static IReadOnlyList<EnumOptionInfo>? ParseEnumOptions(
        JsonElement field, Guid? typeId, IReadOnlyDictionary<Guid, EnumType>? enumTypesById)
    {
        if (field.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
        {
            var list = opts.EnumerateArray()
                .Where(o => o.ValueKind == JsonValueKind.String)
                .Select(o => o.GetString()!)
                .Select(s => new EnumOptionInfo(s, s))
                .ToList();
            if (list.Count > 0) return list;
        }
        if (typeId is { } tid && enumTypesById is not null && enumTypesById.TryGetValue(tid, out var enumType))
            return ParseEnumValues(enumType.Values);
        return null;
    }

    private static List<EnumOptionInfo> ParseEnumValues(JsonDocument values)
    {
        var list = new List<EnumOptionInfo>();
        if (values.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var v in values.RootElement.EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.Object) continue;
            var code = v.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var label = v.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
            if (code is null || label is null) continue;
            list.Add(new EnumOptionInfo(code, label));
        }
        return list;
    }
}
