using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Catalog;
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
}
