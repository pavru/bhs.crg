using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Reconciliation;
using MediatR;

namespace BHS.CRG.Application.Reconciliation;

public class ReconciliationHandlers(
    IRepository<ReconciliationDefinition> definitions,
    IRepository<ReconciliationRun> runs,
    IRepository<ReconciliationFinding> findings,
    IRepository<ReconciliationDecision> decisions,
    IReconciliationRunner runner) :
    IRequestHandler<ListReconciliationsQuery, IReadOnlyList<ReconciliationDefinition>>,
    IRequestHandler<GetReconciliationQuery, ReconciliationDefinition?>,
    IRequestHandler<CreateReconciliationCommand, ReconciliationDefinition>,
    IRequestHandler<UpdateReconciliationCommand, ReconciliationDefinition>,
    IRequestHandler<DeleteReconciliationCommand>,
    IRequestHandler<RunReconciliationCommand, ReconciliationRun>,
    IRequestHandler<ListReconciliationRunsQuery, IReadOnlyList<ReconciliationRun>>,
    IRequestHandler<ListFindingsQuery, IReadOnlyList<FindingView>>,
    IRequestHandler<SetDecisionCommand, ReconciliationDecision>,
    IRequestHandler<RemoveDecisionCommand>
{
    public async Task<IReadOnlyList<ReconciliationDefinition>> Handle(
        ListReconciliationsQuery q, CancellationToken ct)
        => await definitions.FindAsync(d =>
            (!q.Scope.HasValue || d.Scope == q.Scope.Value) &&
            (!q.ScopeId.HasValue || d.ScopeId == q.ScopeId.Value), ct);

    public async Task<ReconciliationDefinition?> Handle(GetReconciliationQuery q, CancellationToken ct)
        => await definitions.GetByIdAsync(q.Id, ct);

    public async Task<ReconciliationDefinition> Handle(CreateReconciliationCommand cmd, CancellationToken ct)
    {
        var d = ReconciliationDefinition.Create(cmd.Name, cmd.Scope, cmd.ScopeId, cmd.Spec);
        await definitions.AddAsync(d, ct);
        await definitions.SaveChangesAsync(ct);
        return d;
    }

    public async Task<ReconciliationDefinition> Handle(UpdateReconciliationCommand cmd, CancellationToken ct)
    {
        var d = await definitions.GetByIdAsync(cmd.Id, ct) ?? throw new KeyNotFoundException();
        d.Update(cmd.Name, cmd.Spec);
        definitions.Update(d);
        await definitions.SaveChangesAsync(ct);
        return d;
    }

    public async Task Handle(DeleteReconciliationCommand cmd, CancellationToken ct)
    {
        var d = await definitions.GetByIdAsync(cmd.Id, ct) ?? throw new KeyNotFoundException();
        definitions.Remove(d);
        await definitions.SaveChangesAsync(ct);
    }

    public async Task<ReconciliationRun> Handle(RunReconciliationCommand cmd, CancellationToken ct)
        => await runner.RunAsync(cmd.DefinitionId, ct);

    public async Task<IReadOnlyList<ReconciliationRun>> Handle(ListReconciliationRunsQuery q, CancellationToken ct)
    {
        var all = await runs.FindAsync(r => r.DefinitionId == q.DefinitionId, ct);
        return [.. all.OrderByDescending(r => r.StartedAt).Take(Math.Max(1, q.Limit))];
    }

    /// <summary>
    /// Находки прогона, дополненные тем, чего в самой находке нет и быть не должно: решением человека
    /// (живёт отдельно и переживает прогоны) и признаком устранения (вычисляется из истории).
    /// </summary>
    public async Task<IReadOnlyList<FindingView>> Handle(ListFindingsQuery q, CancellationToken ct)
    {
        var history = (await runs.FindAsync(
                r => r.DefinitionId == q.DefinitionId && r.Status == ReconciliationRunStatus.Completed, ct))
            .OrderByDescending(r => r.StartedAt)
            .ToList();
        if (history.Count == 0) return [];

        var run = q.RunId is { } id
            ? history.FirstOrDefault(r => r.Id == id)
            : history[0];
        if (run is null) return [];

        var current = await findings.FindAsync(f => f.RunId == run.Id, ct);

        // Предыдущий по времени завершённый прогон — то, относительно чего считается «Устранено».
        var previous = history.FirstOrDefault(r => r.StartedAt < run.StartedAt);
        var previouslyBad = previous is null
            ? []
            : (await findings.FindAsync(f => f.RunId == previous.Id, ct))
                .Where(f => f.Status != FindingStatus.Match)
                .Select(f => f.Key)
                .ToHashSet(StringComparer.Ordinal);

        var byKey = (await decisions.FindAsync(d => d.DefinitionId == q.DefinitionId, ct))
            .ToDictionary(d => d.Key, StringComparer.Ordinal);

        return [.. current
            .OrderBy(f => f.Label, StringComparer.CurrentCulture)
            .Select(f => new FindingView(
                f,
                Resolved: f.Status == FindingStatus.Match && previouslyBad.Contains(f.Key),
                Decision: byKey.GetValueOrDefault(f.Key)))];
    }

    public async Task<ReconciliationDecision> Handle(SetDecisionCommand cmd, CancellationToken ct)
    {
        // Одно решение на позицию: повторное по тому же ключу — правка первого, а не вторая запись,
        // иначе «какое из двух действует» стало бы неопределённым.
        var found = await FindByKeyAsync(cmd.DefinitionId, cmd.Key, ct);

        ReconciliationDecision decision;
        if (found is null)
        {
            decision = ReconciliationDecision.Create(cmd.DefinitionId, cmd.Key, cmd.Kind, cmd.Note, cmd.DecidedBy);
            await decisions.AddAsync(decision, ct);
        }
        else
        {
            decision = found;
            decision.Update(cmd.Kind, cmd.Note, cmd.DecidedBy);
            decisions.Update(decision);
        }

        await decisions.SaveChangesAsync(ct);
        return decision;
    }

    public async Task Handle(RemoveDecisionCommand cmd, CancellationToken ct)
    {
        var existing = await FindByKeyAsync(cmd.DefinitionId, cmd.Key, ct);
        if (existing is null) return; // снятие несуществующего решения — не ошибка, состояние уже такое

        decisions.Remove(existing);
        await decisions.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Поиск по естественному ключу в ДВА шага: <c>FindAsync</c> репозитория не отслеживает сущности,
    /// и правка полученной таким образом копии столкнулась бы с уже отслеживаемым экземпляром
    /// («cannot be tracked because another instance with the same key»). Идентификатор берём без
    /// отслеживания, саму сущность — отслеживаемой.
    /// </summary>
    private async Task<ReconciliationDecision?> FindByKeyAsync(Guid definitionId, string key, CancellationToken ct)
    {
        var id = (await decisions.FindAsync(d => d.DefinitionId == definitionId && d.Key == key, ct))
            .Select(d => (Guid?)d.Id).FirstOrDefault();
        return id is null ? null : await decisions.GetByIdAsync(id.Value, ct);
    }
}
