using System.Data;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Crew;
using BHS.CRG.Modules.Work.Data;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Exchange;

internal sealed class ActualWorksProvider(WorkDbContext db, ReportReads reads, IModulePlanItems planItems) : IActualWorks
{
    public IReadOnlyList<string> AnswerPermissions { get; } = Array.AsReadOnly(new[] { WorkModule.FactsRead });

    public async Task<ContractAnswer<ActualWorksAnswer>> ReadAsync(
        ModuleReader reader, ActualWorksQuery query, CancellationToken ct = default)
    {
        if (ContractAnswer.Refusal<ActualWorksAnswer>(reader, WorkModule.FactsRead) is { } refusal) return refusal;
        if (query.From > query.To)
            throw new InvalidRequestException("Начало периода (from) позже его конца (to).");

        // Подача, приёмка, исправление и утверждение могут менять разные части ответа.
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var inputs = await reads.ActualWorksAsync(reader, query, ct);
        var ids = inputs.AcceptedLines.Select(l => l.PlanItemId).Concat(inputs.CrewLines.Select(l => l.PlanItemId));
        var positions = await CrewPublication.ReadPositionsAsync(planItems, ids, ct);
        await tx.CommitAsync(ct);

        return ContractAnswer.Shown(Aggregate(query, inputs, positions));
    }

    private static ActualWorksAnswer Aggregate(
        ActualWorksQuery query, ActualWorksInputs inputs, IReadOnlyDictionary<Guid, PlanItemKey> positions)
    {
        var items = new List<ActualWork>();
        var withoutPlanItem = 0;
        foreach (var line in inputs.AcceptedLines)
        {
            if (!CrewPublication.PositionMatches(positions, line.PlanItemId,
                    new(line.WorkTypeId, query.ConstructionId, line.SectionId, line.UnitId)))
                withoutPlanItem++;
            else if (!line.IsCrew)
                items.Add(new(line.PlanItemId, line.Date, Report.ShiftCode(line.Shift), line.SectionId, line.Volume));
        }

        var lines = inputs.CrewLines.ToLookup(l => l.GroupId);
        var submitted = inputs.SubmittedEmployees.ToLookup(e => e.GroupId, e => e.EmployeeId);
        var reasons = new Dictionary<CrewFactExclusion, int>();
        var unsubmitted = 0;
        foreach (var group in inputs.Groups)
        {
            var publication = CrewPublication.Classify(group, lines[group.Id].ToArray(), submitted[group.Id], positions);
            unsubmitted += publication.Unsubmitted;
            if (publication.Exclusion is { } reason)
                reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
            if (publication.Published)
            {
                var key = publication.Keys.Single();
                items.Add(new(key.PlanItemId, group.Date, Report.ShiftCode(group.Shift), key.SectionId, group.Total!.Value));
            }
        }

        var wanted = query.PlanItemIds?.ToHashSet();
        var filtered = items.Where(i => (query.SectionId is null || i.SectionId == query.SectionId)
                                       && (wanted is null || wanted.Contains(i.PlanItemId)))
            .GroupBy(i => (i.PlanItemId, i.Date, i.Shift, i.SectionId))
            .Select(g => new ActualWork(g.Key.PlanItemId, g.Key.Date, g.Key.Shift, g.Key.SectionId, g.Sum(i => i.Volume)))
            .OrderBy(i => i.Date).ThenBy(i => i.Shift, StringComparer.Ordinal).ThenBy(i => i.PlanItemId)
            .ToArray();
        var diagnostics = new ActualWorksDiagnostics(
            new(query.ConstructionId, query.From, query.To), inputs.AwaitingAcceptance, reasons.Values.Sum(),
            reasons.OrderBy(r => r.Key).Select(r => new CrewFactExclusionCount(r.Key, r.Value)).ToArray(),
            withoutPlanItem, unsubmitted);
        return new(filtered, diagnostics);
    }
}
