using System.Text.Json;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Generation;

/// <summary>
/// Схема-управляемый штамп метаполя типа объекта в контекст генерации (issue #342). Проставляет
/// <c>data._type</c> самому документу и КАЖДОМУ НЕПУСТОМУ составному объекту (inline complex/union,
/// элементы array/doc-array, вложенные) по <b>объявленному</b> типу поля из схемы.
///
/// Инвариант размещения: ТЕРМИНАЛЬНЫЙ шаг — вызывается ПОСЛЕ <see cref="ResolutionScanner.ScanMissingRequired"/>
/// и union-проверок, поэтому пустые объекты пропускаются (у пустого нет объекта → нет типа) и ни одна
/// проверка пустоты не видит <c>_type</c> (иначе штамп в <c>{}</c> ложно = «заполнено»). Резолвер
/// схема-агностичен — вся схема-осведомлённость локализована здесь.
///
/// Фаза 2: ссылки штампуются резолвером ФАКТИЧЕСКИМ типом записи (для корректного <c>instance-of</c>
/// на подтипах); здесь уже-штампованные объекты пропускаются, домены не пересекаются.
/// </summary>
public static class TypeStamper
{
    public const string MetaKey = "_type";

    /// <summary>Штампует контекст: корень документа + составные поля по эффективной схеме типа.</summary>
    public static void Stamp(GenerationContext ctx, Guid documentTypeId, IReadOnlyDictionary<Guid, DocumentType> byId)
    {
        // Тип самого документа — мастер-диспетч data._type. Корень по пустоте не проверяется → безопасно.
        ctx.Set(MetaKey, TypeMeta.BuildElement(documentTypeId, byId));

        foreach (var f in DocumentTypeSchemaReader.EffectiveFields(documentTypeId, byId))
            if (f.TypeId is { } tid && ctx.Data.TryGetValue(f.Key, out var v) && v is JsonElement je)
                ctx.Set(f.Key, StampField(f.Type, tid, je, byId));
    }

    /// <summary>Штамп значения поля по кардинальности: complex/doc-ref → объект; array/doc-array → массив объектов.</summary>
    private static JsonElement StampField(string fieldType, Guid typeId, JsonElement value, IReadOnlyDictionary<Guid, DocumentType> byId)
    {
        if (DocumentTypeSchemaReader.IsSingleComposite(fieldType))
            return StampObject(value, typeId, byId);
        if (DocumentTypeSchemaReader.IsMultiValued(fieldType) && value.ValueKind == JsonValueKind.Array)
            return JsonSerializer.SerializeToElement(
                value.EnumerateArray().Select(it => StampObject(it, typeId, byId)).ToList());
        return value;
    }

    /// <summary>Штамп одного составного объекта (declared typeId) + рекурсия в его составные подполя.
    /// Не-объект / пустой / уже-штампованный (или ссылка-$ref) — оставляем как есть.</summary>
    private static JsonElement StampObject(JsonElement obj, Guid typeId, IReadOnlyDictionary<Guid, DocumentType> byId)
    {
        if (obj.ValueKind != JsonValueKind.Object) return obj;

        // Пустой = нет НЕ-мета свойств (нет объекта → нет типа). Уже штампованный (_type) — чужой домен
        // (ссылка, проштампованная резолвером). $ref — неразрешённая ссылка, не данные.
        var hasReal = false;
        foreach (var p in obj.EnumerateObject())
        {
            if (p.Name == MetaKey || p.Name == "$ref") return obj;
            if (!p.Name.StartsWith('_')) hasReal = true;
        }
        if (!hasReal) return obj;

        var subFields = DocumentTypeSchemaReader.EffectiveFields(typeId, byId).ToDictionary(sf => sf.Key);
        var dict = new Dictionary<string, JsonElement>();
        foreach (var p in obj.EnumerateObject())
        {
            dict[p.Name] = subFields.TryGetValue(p.Name, out var sf) && sf.TypeId is { } stid
                && (DocumentTypeSchemaReader.IsSingleComposite(sf.Type) || DocumentTypeSchemaReader.IsMultiValued(sf.Type))
                ? StampField(sf.Type, stid, p.Value, byId)
                : p.Value.Clone();
        }
        dict[MetaKey] = TypeMeta.BuildElement(typeId, byId);
        return JsonSerializer.SerializeToElement(dict);
    }
}
