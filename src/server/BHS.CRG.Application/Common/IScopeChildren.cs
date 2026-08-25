using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Common;

/// <summary>
/// ПРЯМЫЕ дети уровня — на один шаг вниз, каждый со своим уровнем.
///
/// Не путать со спуском <see cref="IScopeSubtree"/> («уровень → ВСЕ комплекты поддерева»): своди их
/// вместе, и сводка стройки посчитала бы её комплекты дважды — сама и через разделы. Сводкам нужны
/// именно соседние узлы дерева: они спрашивают у каждого его состояние рекурсивно, и уровень
/// ребёнка — часть вопроса.
///
/// Вынесено из сводки проблем (#454), когда за тем же обходом пришла сводка плана (#796). Второй
/// копии не заводим осознанно: ровно так же троился спуск, из-за чего и появился IScopeSubtree.
/// </summary>
public interface IScopeChildren
{
    Task<IReadOnlyList<(CatalogScope Scope, Guid Id)>> ChildrenOfAsync(
        CatalogScope scope, Guid? scopeId, CancellationToken ct = default);
}

public class ScopeChildren(
    IRepository<Construction> constructions,
    IRepository<Section> sections,
    IRepository<DocumentSet> sets) : IScopeChildren
{
    public async Task<IReadOnlyList<(CatalogScope Scope, Guid Id)>> ChildrenOfAsync(
        CatalogScope scope, Guid? scopeId, CancellationToken ct = default) => scope switch
    {
        CatalogScope.System => [.. (await constructions.GetAllAsync(ct))
            .Select(c => (CatalogScope.Construction, c.Id))],

        CatalogScope.Construction when scopeId is { } id => [.. (await sections.FindAsync(
            s => s.ConstructionId == id, ct)).Select(s => (CatalogScope.Section, s.Id))],

        CatalogScope.Section when scopeId is { } id => [.. (await sets.FindAsync(
            s => s.SectionId == id, ct)).Select(s => (CatalogScope.Set, s.Id))],

        // У комплекта детей в этой оси нет: документы ни проблемами, ни процентом не помечаются —
        // план живёт на комплекте целиком, а маркер на документе читался бы как «ошибка в нём».
        _ => [],
    };
}
