using System.Data;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Crew;
using BHS.CRG.Modules.Work.Data;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Labor;

internal sealed record LaborCrew(CrewGroupRecord Group, CrewPublication Publication, IReadOnlyList<Guid> ReportIds);

internal sealed record LaborSnapshot(IReadOnlyList<LaborReport> Reports, IReadOnlyList<LaborCrew> Crew,
    IReadOnlyDictionary<Guid, PlanItemKey> Positions);

internal sealed class LaborSnapshots(WorkDbContext db, ReportReads reads, IModulePlanItems planItems)
{
    internal async Task<LaborSnapshot> ReadAsync(ModuleReach reach, DateOnly? from, DateOnly? to,
        Guid? workTypeId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var inputs = await reads.LaborAsync(ReportViewer.Of(reach), from, to, workTypeId, ct);
        var positionIds = inputs.Reports.Where(r => r.Accepted).SelectMany(r => r.Lines.Select(l => l.PlanItemId));
        var positions = await CrewPublication.ReadPositionsAsync(planItems,
            positionIds.Concat(inputs.CrewLines.Select(l => l.PlanItemId)), ct);
        await transaction.CommitAsync(ct);

        var lines = inputs.CrewLines.ToLookup(l => l.GroupId);
        var submitted = inputs.SubmittedEmployees.ToLookup(e => e.Shift, e => e.EmployeeId);
        var crew = inputs.Groups.Select(group =>
        {
            var members = lines[group.Id].ToArray();
            var publication = CrewPublication.Classify(group, members, submitted[group.ShiftKey], positions);
            return new LaborCrew(group, publication, members.Select(l => l.ReportId).Distinct().ToArray());
        }).ToArray();
        return new(inputs.Reports, crew, positions);
    }
}
