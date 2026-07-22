using System.Text.Json;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Generation;

/// <summary>
/// Штамп метаполя типа объекта в контекст генерации (issue #342). Проставляет <c>data._type</c> самому
/// документу и КАЖДОМУ НЕПУСТОМУ составному объекту (inline complex/union, элементы array/doc-array,
/// вложенные) по <b>объявленному</b> типу поля из схемы.
///
/// Инвариант размещения: ТЕРМИНАЛЬНЫЙ шаг — вызывается ПОСЛЕ <see cref="ResolutionScanner.ScanMissingRequired"/>
/// и union-проверок, поэтому пустые объекты пропускаются (у пустого нет объекта → нет типа) и ни одна
/// проверка пустоты не видит <c>_type</c> (иначе штамп в <c>{}</c> ложно = «заполнено»). Резолвер
/// схема-агностичен — вся схема-осведомлённость локализована здесь.
///
/// Ссылки помечаются резолвером сырым <c>_typeId</c> = ФАКТИЧЕСКИЙ <c>CompositeTypeId</c> записи (issue #344;
/// резолвер строить chain не может — знает лишь собственный тип объекта). Здесь <c>_typeId</c> разворачивается
/// в полный <c>_type</c> по фактическому типу (корректный <c>instance-of</c> на подтипах: поле «Организация»,
/// запись «Подрядчик»), рекурсия в подполя — по схеме ФАКТИЧЕСКОГО типа.
///
/// Обход идёт по ВСЕМУ дереву контекста, а не только по полям схемы (issue #346): у НЕсхемных/осиротевших
/// ключей (напр. «НовыеРаботы» после удаления поля из типа) сырой <c>_typeId</c> тоже обязан развернуться,
/// иначе он протекает в data.json. Вне схемы объект не штампуется объявленным типом (тип неизвестен), но
/// <c>_typeId</c>-ссылки внутри разворачиваются, а спуск продолжается.
/// </summary>
public static class TypeStamper
{
    public const string MetaKey = "_type";
    /// <summary>Сырой маркер фактического типа ссылки от резолвера — разворачивается здесь в <see cref="MetaKey"/>.</summary>
    public const string TypeIdKey = "_typeId";

    /// <summary>Штампует контекст: корень документа + все верхнеуровневые ключи (схемные — по объявленному
    /// типу; прочие — generic-обход, чтобы развернуть сырые <c>_typeId</c>-ссылки и не оставить их в выводе).</summary>
    public static void Stamp(GenerationContext ctx, Guid documentTypeId, IReadOnlyDictionary<Guid, DocumentType> byId)
    {
        // Тип самого документа — мастер-диспетч data._type. Корень по пустоте не проверяется → безопасно.
        ctx.Set(MetaKey, TypeMeta.BuildElement(documentTypeId, byId));

        var schemaFields = DocumentTypeSchemaReader.EffectiveFields(documentTypeId, byId).ToDictionary(f => f.Key);
        foreach (var key in ctx.Data.Keys.ToList())
        {
            if (key == MetaKey || ctx.Data[key] is not JsonElement je) continue;
            ctx.Set(key, Process(je, CompositeTypeOf(key, schemaFields), byId));
        }
    }

    /// <summary>Объявленный composite-тип поля (для complex/doc-ref/array/doc-array), иначе null.</summary>
    private static Guid? CompositeTypeOf(string key, IReadOnlyDictionary<string, SchemaFieldInfo> fields)
        => fields.TryGetValue(key, out var f) && f.TypeId is { } tid
           && (DocumentTypeSchemaReader.IsSingleComposite(f.Type) || DocumentTypeSchemaReader.IsMultiValued(f.Type))
            ? tid : null;

    /// <summary>
    /// Единый рекурсивный проход по значению. Тип объекта = <c>_typeId</c> (реф, фактический) ⟶ иначе
    /// <paramref name="declaredTypeId"/> (inline по схеме) ⟶ иначе неизвестен (осиротевший/несхемный).
    /// Разворачивает <c>_typeId</c>→<c>_type</c>, штампует непустой inline-составной с известным типом,
    /// и в любом случае спускается вглубь (чтобы ни один сырой <c>_typeId</c> не остался). Пустой объект /
    /// уже-<c>_type</c> / <c>$ref</c> / скаляр — по сути как есть.
    /// </summary>
    private static JsonElement Process(JsonElement v, Guid? declaredTypeId, IReadOnlyDictionary<Guid, DocumentType> byId)
    {
        if (v.ValueKind == JsonValueKind.Array)
            return JsonSerializer.SerializeToElement(
                v.EnumerateArray().Select(it => Process(it, declaredTypeId, byId)).ToList());
        if (v.ValueKind != JsonValueKind.Object) return v;

        Guid? actualFromRef = null;
        var hasReal = false;
        var hasType = false;
        foreach (var p in v.EnumerateObject())
        {
            if (p.Name == "$ref") return v;                              // неразрешённая ссылка — не данные
            if (p.Name == MetaKey) hasType = true;
            else if (p.Name == TypeIdKey) { if (Guid.TryParse(p.Value.GetString(), out var tid)) actualFromRef = tid; }
            else if (!p.Name.StartsWith('_')) hasReal = true;
        }

        var typeId = actualFromRef ?? declaredTypeId;                    // реф → фактический; inline → объявленный
        var subFields = typeId is { } t
            ? DocumentTypeSchemaReader.EffectiveFields(t, byId).ToDictionary(sf => sf.Key)
            : null;

        var dict = new Dictionary<string, JsonElement>();
        foreach (var p in v.EnumerateObject())
        {
            if (p.Name == TypeIdKey) continue;                           // сырой маркер не течёт в вывод
            if (p.Name == MetaKey) { dict[p.Name] = p.Value.Clone(); continue; } // уже штампован — сохраняем
            var childDeclared = subFields is not null ? CompositeTypeOf(p.Name, subFields) : null;
            dict[p.Name] = Process(p.Value, childDeclared, byId);
        }
        // Штампуем _type только если тип известен, объект непустой и ещё не помечен. Пустой/неизвестный — не метим.
        if (typeId is { } tt && hasReal && !hasType)
            dict[MetaKey] = TypeMeta.BuildElement(tt, byId);
        return JsonSerializer.SerializeToElement(dict);
    }
}
