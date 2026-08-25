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
public class ProblemSummaryHandler(IMediator mediator, IScopeChildren scopeChildren)
    : IRequestHandler<GetProblemSummaryQuery, ProblemSummary>
{
    public async Task<ProblemSummary> Handle(GetProblemSummaryQuery q, CancellationToken ct)
    {
        var children = await scopeChildren.ChildrenOfAsync(q.Scope, q.ScopeId, ct);

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
}
