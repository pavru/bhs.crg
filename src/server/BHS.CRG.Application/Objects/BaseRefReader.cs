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

/// <summary>Обратные ссылки между объектами предметной области — для guard'ов удаления.</summary>
public static class DomainObjectReferences
{
    /// <summary>
    /// Другие объекты, ссылающиеся на <paramref name="targetId"/> как на базовый экземпляр
    /// (issue #71) — по «_baseRef» в их Data. Сканирование в памяти (предикат по JSON не
    /// транслируется в SQL); масштаб приложения это допускает, как и прочие guard'ы удаления.
    /// </summary>
    public static async Task<IReadOnlyList<DomainObject>> FindBaseRefReferrersAsync(
        IRepository<DomainObject> repo, Guid targetId, CancellationToken ct)
    {
        var all = await repo.GetAllAsync(ct);
        return all.Where(o => o.Id != targetId && BaseRefReader.GetBaseRefId(o.Data.RootElement) == targetId).ToList();
    }
}
