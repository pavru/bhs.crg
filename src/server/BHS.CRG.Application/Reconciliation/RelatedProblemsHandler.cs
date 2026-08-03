using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Reconciliation;
using MediatR;

namespace BHS.CRG.Application.Reconciliation;

/// <summary>
/// Проблемы, относящиеся к уровню иерархии (issue #452).
///
/// Находки и замечания сводятся ТОЛЬКО в этой проекции чтения, но не в хранении: находка —
/// пересчитываемый снимок арифметики, замечание — персистированное утверждение агента. Слить их в
/// одну сущность значило бы вернуть смешение, запрещённое дизайном сверки (#414).
/// </summary>
public class RelatedProblemsHandler(
    IProblemAttribution attribution,
    IScopeSubtree scopeSubtree,
    IMediator mediator) : IRequestHandler<GetRelatedProblemsQuery, RelatedProblems>
{
    public async Task<RelatedProblems> Handle(GetRelatedProblemsQuery q, CancellationToken ct)
    {
        var ids = await attribution.ReconciliationIdsForAsync(q.Scope, q.ScopeId, ct);

        var definitions = (await mediator.Send(new ListReconciliationsQuery(null, null), ct))
            .Where(d => ids.Contains(d.Id))
            .ToList();

        var related = new List<RelatedReconciliation>();
        var unresolved = 0;

        foreach (var d in definitions)
        {
            // Агрегаты прогона тут не годятся: они не знают о решениях человека, а бейдж обязан
            // обнуляться его действиями.
            var findings = await mediator.Send(new ListFindingsQuery(d.Id), ct);
            var open = findings.Count(f => f.Finding.Status != FindingStatus.Match && f.Decision is null);
            unresolved += open;

            var runs = await mediator.Send(new ListReconciliationRunsQuery(d.Id, 1), ct);
            related.Add(new RelatedReconciliation(d.Id, d.Name, open, runs.FirstOrDefault()?.StartedAt));
        }

        // Считаем только New: отозванное агентом разбирать больше нечего, а неснимаемый счётчик
        // обесценивает бейдж (#459).
        //
        // Замечания адресованы КОМПЛЕКТУ, поэтому выше их надо сводить: без свода на стройке
        // счётчик показал бы ноль при тринадцати неразобранных этажом ниже.
        //
        // ScopeChain для этого не годится: он отвечает «видно ли отсюда», и System-запись
        // засчиталась бы каждому уровню.
        var sets = await scopeSubtree.SetIdsUnderAsync(q.Scope, q.ScopeId, ct);
        var observations = 0;
        foreach (var setId in sets)
            observations += (await mediator.Send(
                new ListObservationsQuery(CatalogScope.Set, setId, ObservationStatus.New), ct)).Count;

        return new RelatedProblems(
            [.. related.OrderByDescending(r => r.UnresolvedFindings).ThenBy(r => r.Name)],
            unresolved,
            observations);
    }
}
