using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Objects;

namespace BHS.CRG.Application.Objects;

/// <summary>
/// Разбор ссылки «_baseRef» (базовый экземпляр, issue #71): дискриминированный объект {kind,id}
/// или голая id-строка (legacy). Единый источник правила для резолвера генерации
/// (<c>EntityResolver</c>) и guard'ов удаления — чтобы «на что ссылается base» трактовалось одинаково.
/// </summary>
public static class BaseRefReader
{
    /// <summary>id из значения «_baseRef» ({kind,id} или голая строка), либо null.</summary>
    public static Guid? ParseRef(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String)
            return Guid.TryParse(el.GetString(), out var g) ? g : null;
        if (el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty("id", out var idEl) && Guid.TryParse(idEl.GetString(), out var gid))
            return gid;
        return null;
    }

    /// <summary>id базового объекта, на который ссылается data через «_baseRef», либо null.</summary>
    public static Guid? GetBaseRefId(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        if (!data.TryGetProperty("_baseRef", out var el)) return null;
        return ParseRef(el);
    }

    /// <summary>
    /// Слияние двух JSON-объектов для _baseRef-наследования: базовые поля первыми, собственные
    /// переопределяют их на верхнем уровне; ключ «_baseRef» исключается. Чистая функция — единый
    /// источник для резолвера генерации (<c>EntityResolver</c>) и flatten при копировании (issue #283).
    /// </summary>
    public static JsonElement MergeObjects(JsonElement baseData, JsonElement ownData)
    {
        var merged = new Dictionary<string, JsonElement>();
        if (baseData.ValueKind == JsonValueKind.Object)
            foreach (var p in baseData.EnumerateObject())
                if (p.Name != "_baseRef") merged[p.Name] = p.Value.Clone();
        if (ownData.ValueKind == JsonValueKind.Object)
            foreach (var p in ownData.EnumerateObject())
                if (p.Name != "_baseRef") merged[p.Name] = p.Value.Clone();
        return JsonSerializer.SerializeToElement(merged);
    }
}

/// <summary>
/// Чтение ссылок «$ref» в значениях полей (issue #269): resolve-объекты
/// {$ref:"catalog", entryId} / {$ref:"document"|"instance", instanceId}, которые EntityResolver
/// разворачивает при генерации. Указывают на другой <c>DomainObject</c> (запись общих данных или
/// документ). Могут лежать на любой глубине Data (вложенные объекты, массивы) — обход рекурсивный.
/// </summary>
public static class RefReader
{
    /// <summary>id всех объектов, на которые ссылается data через «$ref», рекурсивно.</summary>
    public static IEnumerable<Guid> CollectRefIds(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                if (el.TryGetProperty("$ref", out var rt) && rt.ValueKind == JsonValueKind.String)
                {
                    // id живёт в entryId (catalog) либо instanceId (document/instance).
                    var idProp = rt.GetString() == "catalog" ? "entryId" : "instanceId";
                    if (el.TryGetProperty(idProp, out var idEl) && Guid.TryParse(idEl.GetString(), out var g))
                        yield return g;
                }
                foreach (var p in el.EnumerateObject())
                    foreach (var id in CollectRefIds(p.Value)) yield return id;
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    foreach (var id in CollectRefIds(item)) yield return id;
                break;
        }
    }
}

/// <summary>Обратные ссылки между объектами предметной области — для guard'ов удаления.</summary>
public static class DomainObjectReferences
{
    /// <summary>
    /// Другие объекты, ссылающиеся на <paramref name="targetId"/>: как на базовый экземпляр
    /// («_baseRef», issue #71) ИЛИ через «$ref» в значениях полей (issue #269 — doc-ref/@@ref).
    /// Сканирование в памяти (предикат по JSON не транслируется в SQL); масштаб приложения это
    /// допускает, как и прочие guard'ы удаления.
    /// </summary>
    public static async Task<IReadOnlyList<DomainObject>> FindReferrersAsync(
        IRepository<DomainObject> repo, Guid targetId, CancellationToken ct)
    {
        var all = await repo.GetAllAsync(ct);
        return all.Where(o => o.Id != targetId && ReferencesObject(o.Data.RootElement, targetId)).ToList();
    }

    private static bool ReferencesObject(JsonElement data, Guid targetId)
        => BaseRefReader.GetBaseRefId(data) == targetId
           || RefReader.CollectRefIds(data).Contains(targetId);
}

/// <summary>
/// Скраб исходящих ссылок при копировании/переносе документа в ДРУГОЙ комплект (issue #283, стратегия
/// B «умная очистка»): убирает значения-ссылки `$ref:document/instance` — они структурно same-set и в
/// чужом комплекте не резолвятся (дали бы сырой `{$ref}` = мусор в PDF). `$ref:catalog` НЕ трогает
/// (валидность в новом scope проверяется отдельно, для предупреждений). Чистая функция.
/// </summary>
public static class RefScrubber
{
    /// <summary>
    /// Очищенная копия data без doc/instance-ссылок + ключи полей верхнего уровня, чьё значение убрано.
    /// </summary>
    public static (JsonElement Data, IReadOnlyList<string> StrippedFields) StripInstanceRefs(JsonElement data)
    {
        var stripped = new List<string>();
        var result = Strip(data, topLevel: true, stripped) ?? JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>());
        return (result, stripped);
    }

    // Возвращает null, если узел САМ — doc/instance-ссылка (должен быть удалён вызывающим).
    private static JsonElement? Strip(JsonElement el, bool topLevel, List<string> stripped)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                if (IsInstanceRef(el)) return null;
                var obj = new Dictionary<string, JsonElement>();
                foreach (var p in el.EnumerateObject())
                {
                    var child = Strip(p.Value, topLevel: false, stripped);
                    if (child is { } c) obj[p.Name] = c;
                    else if (topLevel && !stripped.Contains(p.Name)) stripped.Add(p.Name);
                }
                return JsonSerializer.SerializeToElement(obj);
            case JsonValueKind.Array:
                var arr = new List<JsonElement>();
                foreach (var item in el.EnumerateArray())
                    if (Strip(item, topLevel: false, stripped) is { } c) arr.Add(c);
                return JsonSerializer.SerializeToElement(arr);
            default:
                return el.Clone();
        }
    }

    private static bool IsInstanceRef(JsonElement el)
        => el.TryGetProperty("$ref", out var r) && r.ValueKind == JsonValueKind.String
           && r.GetString() is "document" or "instance";
}
