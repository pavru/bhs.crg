using BHS.CRG.Domain.Catalog;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Common;

/// <summary>
/// Скоп-цепочка комплекта: (комплект, раздел, стройка) — единое кодирование «оси расположения»
/// объекта (issue #73, код-фаза). Заменяет разрозненные копии резолва/фильтра, которые раньше
/// жили приватно в <c>EntityResolver</c> и <c>DataSetResolver</c>.
/// </summary>
public readonly record struct ScopeChain(Guid SetId, Guid SectionId, Guid ConstructionId)
{
    /// <summary>
    /// Входит ли запись со скопом (<paramref name="scope"/>, <paramref name="scopeId"/>) в
    /// скоп-поддерево этого комплекта: System — всегда; иначе (Scope,ScopeId) совпадает с
    /// комплектом/разделом/стройкой. Иные значения — не входят.
    /// </summary>
    public bool Contains(CatalogScope scope, Guid? scopeId) => scope switch
    {
        CatalogScope.System => true,
        CatalogScope.Set => scopeId == SetId,
        CatalogScope.Section => scopeId == SectionId,
        CatalogScope.Construction => scopeId == ConstructionId,
        _ => false,
    };
}

public static class ScopeChains
{
    /// <summary>Резолвит скоп-цепочку комплекта: комплект → раздел → стройка (2 lookup, AsNoTracking).</summary>
    public static async Task<ScopeChain> LoadAsync(AppDbContext db, Guid setId, CancellationToken ct)
    {
        var set = await db.DocumentSets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == setId, ct);
        var sectionId = set?.SectionId ?? Guid.Empty;
        var section = sectionId == Guid.Empty ? null
            : await db.Sections.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sectionId, ct);
        return new ScopeChain(setId, sectionId, section?.ConstructionId ?? Guid.Empty);
    }
}
