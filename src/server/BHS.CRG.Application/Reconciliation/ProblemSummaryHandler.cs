using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using MediatR;

namespace BHS.CRG.Application.Reconciliation;

/// <summary>
/// Счётчики проблем для маркеров навигации (issue #454).
///
/// Отдаёт свой уровень И разбивку по непосредственным детям одним ответом: маркер нужен именно на
/// пунктах, ведущих вниз, и запрос-на-ребёнка превратил бы страницу раздела с десятью комплектами в
/// десять обращений.
/// </summary>
public class ProblemSummaryHandler(
    IMediator mediator,
    IRepository<Domain.Documents.Construction> constructions,
    IRepository<Domain.Documents.Section> sections,
    IRepository<Domain.Documents.DocumentSet> sets)
    : IRequestHandler<GetProblemSummaryQuery, ProblemSummary>
{
    public async Task<ProblemSummary> Handle(GetProblemSummaryQuery q, CancellationToken ct)
    {
        var children = await ChildrenOfAsync(q.Scope, q.ScopeId, ct);

        var counts = new List<ProblemCount>();
        foreach (var (childScope, childId) in children)
        {
            var p = await mediator.Send(new GetRelatedProblemsQuery(childScope, childId), ct);
            // Нулевой маркер не рисуется, поэтому и в ответе его нет: он существует, чтобы отличать
            // «есть что разобрать» от «пусто», а нарисованный ноль этой задачи не решает.
            if (p.NeedsAttention > 0)
                counts.Add(new ProblemCount(childId, p.NeedsAttention, p.HasArithmeticProblems));
        }

        // Для System своего уровня нет — он сумма всех строек.
        if (q.Scope == CatalogScope.System || q.ScopeId is not { } selfId)
            return new ProblemSummary(
                counts.Sum(c => c.NeedsAttention), counts.Any(c => c.HasArithmeticProblems), counts);

        var self = await mediator.Send(new GetRelatedProblemsQuery(q.Scope, selfId), ct);
        return new ProblemSummary(self.NeedsAttention, self.HasArithmeticProblems, counts);
    }

    /// <summary>
    /// ПРЯМЫЕ дети уровня — на один шаг вниз, и это не то же самое, что спуск
    /// <see cref="Common.IScopeSubtree"/> «уровень → все комплекты поддерева» (issue #625).
    ///
    /// Своди их вместе — и сводка стройки посчитала бы её комплекты дважды: сама и через разделы.
    /// Здесь нужны именно соседние узлы дерева, каждый со своим уровнем: сводка спрашивает у них
    /// состояние рекурсивно, и уровень ребёнка — часть ответа.
    /// </summary>
    private async Task<IReadOnlyList<(CatalogScope Scope, Guid Id)>> ChildrenOfAsync(
        CatalogScope scope, Guid? scopeId, CancellationToken ct) => scope switch
    {
        CatalogScope.System => [.. (await constructions.GetAllAsync(ct))
            .Select(c => (CatalogScope.Construction, c.Id))],

        CatalogScope.Construction when scopeId is { } id => [.. (await sections.FindAsync(
            s => s.ConstructionId == id, ct)).Select(s => (CatalogScope.Section, s.Id))],

        CatalogScope.Section when scopeId is { } id => [.. (await sets.FindAsync(
            s => s.SectionId == id, ct)).Select(s => (CatalogScope.Set, s.Id))],

        // У комплекта детей в этой оси нет: документы проблемами не помечаются — связь находки с
        // документом слабее, чем выглядит, и маркер на нём читался бы как «ошибка в документе».
        _ => [],
    };
}
