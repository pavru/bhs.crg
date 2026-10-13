using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Objects;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Persistence;

/// <summary>
/// Чтения пакетной очистки (issue #1245): названия без данных. Записи справочника несут картинки, а
/// предпросмотру пакета нужны идентификатор, название и признак архива.
/// </summary>
public partial class DomainObjectRepository
{
    public async Task<IReadOnlyList<CommonDataRef>> LiveRefsOfTypeAsync(
        Guid typeId, CatalogScope scope, Guid? scopeId, CancellationToken ct = default) =>
        // Готовый отбор берёт действующие записи (решение владельца 12.10.2026): архивные удаляют,
        // назвав их, — выбором в списке.
        await Db.Set<DomainObject>().AsNoTracking()
            .Where(o => o.Facet == null && o.ArchivedAt == null && o.CompositeTypeId == typeId
                        && o.ScopeLevel == scope && o.ScopeId == scopeId)
            .Select(o => new CommonDataRef(o.Id, o.CompositeTypeId, o.DisplayName, false, null))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> RecordIdsOfTypesAsync(
        IReadOnlyCollection<Guid> typeIds, CancellationToken ct = default)
    {
        if (typeIds.Count == 0) return [];
        // Архивные — тоже: ссылка на архивную запись держит её так же, как на действующую.
        return await Db.Set<DomainObject>().AsNoTracking()
            .Where(o => o.Facet == null && typeIds.Contains(o.CompositeTypeId))
            .Select(o => o.Id)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CommonDataRef>> RefsAmongAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];
        // Названные записи — все, и архивные: человек выбрал их сам, признак едет с каждой.
        return await Db.Set<DomainObject>().AsNoTracking()
            .Where(o => o.Facet == null && ids.Contains(o.Id))
            .Select(o => new CommonDataRef(o.Id, o.CompositeTypeId, o.DisplayName, o.ArchivedAt != null, null))
            .ToListAsync(ct);
    }
}
