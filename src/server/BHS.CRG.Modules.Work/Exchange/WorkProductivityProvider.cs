using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Work.Labor;

namespace BHS.CRG.Modules.Work.Exchange;

internal sealed class WorkProductivityProvider(LaborSnapshots snapshots, Productivity productivity) : IWorkProductivity
{
    public IReadOnlyList<string> AnswerPermissions { get; } = Array.AsReadOnly(new[] { WorkModule.FactsRead });

    public async Task<ContractAnswer<WorkProductivityAnswer>> ReadAsync(
        ModuleReader reader, WorkProductivityQuery query, CancellationToken ct = default)
    {
        if (ContractAnswer.Refusal<WorkProductivityAnswer>(reader, WorkModule.FactsRead) is { } refusal) return refusal;
        if (query.WorkTypeId == Guid.Empty || query.UnitId == Guid.Empty)
            throw new InvalidRequestException("Выработка требует вид работы (workTypeId) и единицу (unitId).");

        var snapshot = await snapshots.ReadAsync(ModuleReach.All, null, null, query.WorkTypeId, ct);
        var values = (await productivity.CalculateAsync(snapshot, query, sectionId: null, crossModule: true, ct)).Productivity;
        var state = values.Months.Any(m => m.Productivity.State == LaborStates.Ok) ? LaborStates.Ok : LaborStates.TooLittleData;
        return ContractAnswer.Shown(new WorkProductivityAnswer(values.UnitId, values.MinSample, state, values.Months));
    }
}
