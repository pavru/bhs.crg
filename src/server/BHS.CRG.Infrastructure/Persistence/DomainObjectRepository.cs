using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Persistence;

/// <summary>
/// Репозиторий единого <see cref="DomainObject"/> (issue #84). Грузит документную фасету и её
/// сгенерированные файлы — для общих данных фасета просто отсутствует (null).
/// </summary>
public class DomainObjectRepository(AppDbContext db) : Repository<DomainObject>(db), IDomainObjectRepository
{
    public override Task<DomainObject?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Db.Set<DomainObject>()
            .Include(o => o.Facet)
            .ThenInclude(f => f!.GeneratedFiles)
            .FirstOrDefaultAsync(o => o.Id == id, ct);

    public async Task<IReadOnlyList<DomainObject>> GetSetDocumentsAsync(Guid setId, bool tracked, CancellationToken ct = default)
    {
        var q = Db.Set<DomainObject>()
            .Include(o => o.Facet).ThenInclude(f => f!.GeneratedFiles)
            .Where(o => o.ScopeLevel == CatalogScope.Set && o.ScopeId == setId && o.Facet != null);
        if (!tracked) q = q.AsNoTracking();
        return await q.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DomainObject>> GetDocumentsInSetsAsync(IReadOnlyCollection<Guid> setIds, CancellationToken ct = default)
        => await Db.Set<DomainObject>()
            .AsNoTracking()
            .Include(o => o.Facet)
            .Where(o => o.ScopeLevel == CatalogScope.Set && o.ScopeId != null && setIds.Contains(o.ScopeId.Value) && o.Facet != null)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<DomainObject>> GetDocumentsOfTypeAsync(Guid documentTypeId, CancellationToken ct = default)
        => await Db.Set<DomainObject>()
            .Include(o => o.Facet).ThenInclude(f => f!.GeneratedFiles)
            .Where(o => o.CompositeTypeId == documentTypeId && o.Facet != null)
            .ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, int>> CountDocumentsInSetsAsync(IReadOnlyCollection<Guid> setIds, CancellationToken ct = default)
    {
        if (setIds.Count == 0) return new Dictionary<Guid, int>();
        // Только COUNT по оси (Set, ScopeId) с наличием фасеты — без загрузки Data/JSONB (лёгкий счётчик
        // для навигации/каскадов; сами документы — DomainObject по расположению, прямой навигации нет).
        var rows = await Db.Set<DomainObject>()
            .AsNoTracking()
            .Where(o => o.ScopeLevel == CatalogScope.Set && o.ScopeId != null && setIds.Contains(o.ScopeId.Value) && o.Facet != null)
            .GroupBy(o => o.ScopeId!.Value)
            .Select(g => new { SetId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.SetId, r => r.Count);
    }

    public async Task<IReadOnlyDictionary<(Guid SetId, Guid TypeId), int>> CountReadyDocumentsByTypeAsync(
        IReadOnlyCollection<Guid> setIds, CancellationToken ct = default)
    {
        if (setIds.Count == 0) return new Dictionary<(Guid, Guid), int>();

        var rows = await Db.Set<DomainObject>()
            .AsNoTracking()
            .Where(o => o.ScopeLevel == CatalogScope.Set && o.ScopeId != null && setIds.Contains(o.ScopeId.Value)
                        && o.Facet != null && o.Facet.Status == DocumentStatus.Generated)
            .GroupBy(o => new { SetId = o.ScopeId!.Value, TypeId = o.CompositeTypeId })
            .Select(g => new { g.Key.SetId, g.Key.TypeId, Count = g.Count() })
            .ToListAsync(ct);

        return rows.ToDictionary(r => (r.SetId, r.TypeId), r => r.Count);
    }

    public async Task<IReadOnlyList<CommonDataRef>> FindCommonDataRefsAsync(
        IReadOnlyCollection<Guid> typeIds, string? search, IReadOnlyCollection<Guid>? ids, int? limit,
        CancellationToken ct = default)
    {
        if (typeIds.Count == 0) return [];

        // Только общие данные: документная фасета здесь ни при чём — у записи справочника её нет, а
        // без этого условия в выбор позиции попали бы документы комплектов.
        var query = Db.Set<DomainObject>()
            .AsNoTracking()
            .Where(o => o.Facet == null && typeIds.Contains(o.CompositeTypeId));

        if (ids is { Count: > 0 }) query = query.Where(o => ids.Contains(o.Id));

        if (!string.IsNullOrWhiteSpace(search))
        {
            // ⚠️ Знаки образца обезврежены: «%» в набранном тексте иначе означал бы «что угодно», то
            // есть поиск «100%» показывал бы всё подряд и выглядел бы исправной работой.
            var pattern = "%" + search.Trim().Replace("!", "!!").Replace("%", "!%").Replace("_", "!_") + "%";
            // ⚠️ И по альтернативным именам (issue #1169): они «участвуют в сопоставлении по имени
            // наравне с названием» (DomainObject.Aliases). Без них запрос «ВВГ 3*2.5» к позиции
            // «Кабель ВВГнг(А)-LS 3х2,5» отвечал пустым списком — и человек заводил дубль.
            query = query.Where(o => EF.Functions.ILike(o.DisplayName!, pattern, "!")
                                     || o.Aliases.Any(a => EF.Functions.ILike(a, pattern, "!")));
        }

        // Сортировка ДО отсечения — иначе «первые N по названию» означало бы «произвольные N»
        // (контракт метода). Сравнение здесь базы, окончательный порядок задаёт тот, кто показывает.
        var ordered = query.OrderBy(o => o.DisplayName).AsQueryable();
        if (limit is > 0) ordered = ordered.Take(limit.Value);

        // Альтернативные имена едут только когда искали текстом: назвать «найдено по …» больше
        // некому, а без поиска они ссылке не нужны.
        if (string.IsNullOrWhiteSpace(search))
            return await ordered
                .Select(o => new CommonDataRef(o.Id, o.CompositeTypeId, o.DisplayName, null))
                .ToListAsync(ct);

        var needle = search.Trim();
        var found = await ordered
            .Select(o => new { o.Id, o.CompositeTypeId, o.DisplayName, o.Aliases })
            .ToListAsync(ct);

        return [.. found.Select(o => new CommonDataRef(o.Id, o.CompositeTypeId, o.DisplayName,
            MatchedAlias(o.DisplayName, o.Aliases, needle)))];
    }

    /// <summary>
    /// По какому альтернативному имени запись найдена — если набранного нет в её названии.
    ///
    /// <para>Сравнение здесь своё, а не базы, и разойтись с <c>ILIKE</c> оно может на редких знаках.
    /// Расхождение безвредно: запись всё равно в ответе (её отобрала база), пропадёт только пояснение.</para>
    /// </summary>
    private static string? MatchedAlias(string? name, IReadOnlyList<string> aliases, string needle) =>
        name is not null && name.Contains(needle, StringComparison.OrdinalIgnoreCase)
            ? null
            : aliases.FirstOrDefault(a => a.Contains(needle, StringComparison.OrdinalIgnoreCase));
}
