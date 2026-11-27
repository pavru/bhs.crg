using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Work.Labor;

namespace BHS.CRG.Modules.Work.Exchange;

internal sealed class WorkProductivityProvider(LaborSnapshots snapshots, Productivity productivity) : IWorkProductivity
{
    public IReadOnlyList<string> AnswerPermissions { get; } = Array.AsReadOnly(new[] { WorkModule.FactsRead });

    public async Task<IReadOnlyDictionary<WorkProductivityQuery, ContractAnswer<WorkProductivityAnswer>>> ReadManyAsync(
        ModuleReader reader, IReadOnlyCollection<WorkProductivityQuery> queries, CancellationToken ct = default)
    {
        var distinct = queries.Distinct().ToArray();
        if (ContractAnswer.Refusal<WorkProductivityAnswer>(reader, WorkModule.FactsRead) is { } refusal)
            return distinct.ToDictionary(query => query, _ => refusal);
        if (distinct.Length == 0)
            return new Dictionary<WorkProductivityQuery, ContractAnswer<WorkProductivityAnswer>>();
        if (distinct.Any(query => query.WorkTypeId == Guid.Empty || query.UnitId == Guid.Empty))
            throw new InvalidRequestException("Выработка требует вид работы (workTypeId) и единицу (unitId).");

        var workTypeIds = distinct.Select(query => query.WorkTypeId).Distinct().ToArray();
        var snapshot = await snapshots.ReadAsync(ModuleReach.All, null, null, workTypeIds, ct);
        var answers = await productivity.CalculateMonthlyAsync(snapshot, distinct, ct);
        return answers.ToDictionary(item => item.Key, item => ContractAnswer.Shown(item.Value));
    }
}
