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
