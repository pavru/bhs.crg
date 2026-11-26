using System.Text.Json.Nodes;
using BHS.CRG.Modules.Work.Data;

namespace BHS.CRG.Modules.Work.Crew;

internal sealed record CrewGroupLine(Guid GroupId, Guid ReportId, Guid EmployeeId, Guid Id, Guid? VariantId,
    decimal Volume, Guid UnitId, string CrewRole, IReadOnlyList<Guid> Participants,
    Guid PlanItemId, Guid? SectionId, bool Accepted);

internal sealed record CrewSubmittedEmployee(Guid GroupId, Guid EmployeeId);

public sealed class CrewInputs(WorkDbContext db, ReportReads reads)
{
    internal Task LockAsync(Report report, CancellationToken ct) =>
        db.LockCrewShiftAsync(report.ConstructionId, report.Date, report.Shift, ct);

    internal Task SubmissionChangedAsync(Report report, CancellationToken ct) =>
        TouchAsync(report, report.Lines.Where(l => l.IsCrew).Select(l => l.WorkTypeId), ct);

    internal Task LinesChangedAsync(Report report, ReportSnapshot before, CancellationToken ct)
    {
        var after = ReportJournal.Capture(report);
        var changedLines = before.Values.Keys.Concat(after.Values.Keys)
            .Where(k => k.Line != null && !JsonNode.DeepEquals(before.Values.GetValueOrDefault(k), after.Values.GetValueOrDefault(k)))
            .Select(k => k.Line).Distinct();
        var workTypes = changedLines.SelectMany(line => new[] { CrewType(before, line), CrewType(after, line) })
            .OfType<Guid>();
        return TouchAsync(report, workTypes, ct);
    }

    private static Guid? CrewType(ReportSnapshot snapshot, Guid? line) =>
        snapshot.Values.GetValueOrDefault((line, "isCrew"))?.GetValue<bool>() == true
            ? Guid.Parse(snapshot.Values[(line, "workTypeId")]!.GetValue<string>())
            : null;

    private async Task TouchAsync(Report report, IEnumerable<Guid> affectedTypes, CancellationToken ct)
    {
        var workTypes = affectedTypes.Distinct().ToArray();
        if (workTypes.Length == 0) return;
        var existing = (await reads.CrewGroupsOnShiftAsync(report, workTypes, ct)).ToDictionary(g => g.WorkTypeId);
        foreach (var type in workTypes)
        {
            if (existing.TryGetValue(type, out var group)) group.InputsChanged();
            else db.Add(CrewGroupRecord.Create(report, type));
        }
    }
}
