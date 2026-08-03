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

    /// <summary>
    /// Скоп-цепочка для объекта на произвольном уровне оси (issue #99): от его расположения
    /// (ScopeLevel, ScopeId) резолвит родителей вверх (Section→Construction). Неразрешённые уровни —
    /// Guid.Empty. Нужна для резолва @@ref-привязок общих данных (у них нет комплекта-владельца).
    /// </summary>
    public static async Task<ScopeChain> LoadForScopeAsync(AppDbContext db, CatalogScope scopeLevel, Guid? scopeId, CancellationToken ct)
    {
        switch (scopeLevel)
        {
            case CatalogScope.Set when scopeId is { } sid:
                return await LoadAsync(db, sid, ct);
            case CatalogScope.Section when scopeId is { } secId:
                var section = await db.Sections.AsNoTracking().FirstOrDefaultAsync(s => s.Id == secId, ct);
                return new ScopeChain(Guid.Empty, secId, section?.ConstructionId ?? Guid.Empty);
            case CatalogScope.Construction when scopeId is { } cid:
                return new ScopeChain(Guid.Empty, Guid.Empty, cid);
            default: // System — родителей нет
                return new ScopeChain(Guid.Empty, Guid.Empty, Guid.Empty);
        }
    }
}

/// <summary>
/// Спуск по той же оси, что <see cref="ScopeChains"/> ходит вверх: «уровень → все комплекты под ним»
/// (issue #625). Симметрия нарочно рядом — их читают вместе.
///
/// До этого спуск был написан трижды: в <c>ListAvailableInstancesQuery</c> (два запроса через
/// репозитории), в <c>ProblemAttributionService</c> (лучшая копия, но запертая в контракте разбора
/// проблем сверки) и — на ОДИН уровень — в <c>ProblemSummaryHandler.ChildrenOfAsync</c>. Последний
/// не трогаем: ему нужны прямые дети уровня для дерева сводки, а не всё поддерево; сведи их вместе —
/// и в сводке стройки комплекты посчитались бы дважды (сами и через разделы).
/// </summary>
public static class ScopeSubtree
{
    /// <inheritdoc cref="Application.Common.IScopeSubtree.SetIdsUnderAsync" />
    public static async Task<IReadOnlyList<Guid>> SetIdsUnderAsync(
        AppDbContext db, CatalogScope scope, Guid? scopeId, CancellationToken ct = default) => scope switch
    {
        // Комплект — сам себе поддерево, и это ответ СТРУКТУРНЫЙ: существование строки не
        // проверяем. Проверка выглядит уместной (у соседних уровней неизвестный идентификатор даёт
        // пусто сам собой), но меняет смысл: счётчик замечаний спрашивает «какие комплекты
        // опросить», замечания живут по идентификатору области, и на удалённом комплекте они
        // просто перестали бы считаться. Отличие уровней описано в контракте IScopeSubtree.
        CatalogScope.Set when scopeId is { } setId => [setId],
        CatalogScope.Section when scopeId is { } sectionId => await db.DocumentSets.AsNoTracking()
            .Where(s => s.SectionId == sectionId).Select(s => s.Id).ToListAsync(ct),
        CatalogScope.Construction when scopeId is { } constructionId => await db.DocumentSets.AsNoTracking()
            .Where(s => db.Sections.Any(sec => sec.Id == s.SectionId && sec.ConstructionId == constructionId))
            .Select(s => s.Id).ToListAsync(ct),
        _ => [],
    };

    /// <summary>Комплект поддерева вместе с именами — своим и своего раздела.</summary>
    public readonly record struct SetInSubtree(Guid SetId, string SetName, Guid SectionId, string SectionName);

    /// <summary>
    /// То же поддерево, но с именами комплекта и раздела: реестру раздела или стройки они нужны
    /// колонками, и брать их вторым запросом по уже найденным идентификаторам значило бы спрашивать
    /// базу о том, что она только что вернула.
    /// </summary>
    public static async Task<IReadOnlyList<SetInSubtree>> SetsUnderAsync(
        AppDbContext db, CatalogScope scope, Guid? scopeId, CancellationToken ct = default)
    {
        var query = from set in db.DocumentSets.AsNoTracking()
                    join section in db.Sections.AsNoTracking() on set.SectionId equals section.Id
                    select new { set.Id, SetName = set.Name, SectionId = section.Id, SectionName = section.Name, section.ConstructionId };

        query = scope switch
        {
            CatalogScope.Set when scopeId is { } setId => query.Where(x => x.Id == setId),
            CatalogScope.Section when scopeId is { } sectionId => query.Where(x => x.SectionId == sectionId),
            CatalogScope.Construction when scopeId is { } cid => query.Where(x => x.ConstructionId == cid),
            _ => query.Where(_ => false), // System и уровень без объекта — не поддерево
        };

        return [.. (await query.ToListAsync(ct))
            .Select(x => new SetInSubtree(x.Id, x.SetName, x.SectionId, x.SectionName))];
    }
}

/// <summary>Тот же спуск для слоя приложения, которому <c>AppDbContext</c> недоступен.</summary>
public class ScopeSubtreeService(AppDbContext db) : Application.Common.IScopeSubtree
{
    public Task<IReadOnlyList<Guid>> SetIdsUnderAsync(CatalogScope scope, Guid? scopeId, CancellationToken ct = default)
        => ScopeSubtree.SetIdsUnderAsync(db, scope, scopeId, ct);
}
