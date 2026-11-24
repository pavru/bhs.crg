using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Calendars;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Endpoints;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Schedules;

internal sealed class ScheduleViews(PlanDbContext db, IModulePlanItems items, IModuleUnits units, IModuleEmployees employees)
{
    internal async Task<Schedule> LoadAsync(Guid id, CancellationToken ct) =>
        await db.Schedules.SingleOrDefaultAsync(s => s.Id == id, ct)
        ?? throw new NotFoundException("График не найден.");

    internal Task<List<ScheduleWork>> WorksAsync(Guid id, CancellationToken ct) =>
        db.ScheduleWorks.Where(w => w.ScheduleId == id)
            .OrderBy(w => w.Name).ThenBy(w => w.SectionId).ThenBy(w => w.Id).ToListAsync(ct);

    internal async Task<ScheduleDto> ReadAsync(Schedule schedule, ScheduleSource? source, CancellationToken ct)
    {
        var works = await WorksAsync(schedule.Id, ct);
        var workIds = works.Select(w => w.Id).ToArray();
        var assignees = await db.ScheduleWorkAssignees.AsNoTracking().Where(a => workIds.Contains(a.WorkId))
            .OrderBy(a => a.EmployeeId).ToListAsync(ct);
        var employeeIds = assignees.ToLookup(a => a.WorkId, a => a.EmployeeId);
        var positions = (await items.GetAsync(works.Select(w => w.PlanItemId).Distinct().ToArray(), ct))
            .ToDictionary(p => p.Id, p => p.Key);
        var relations = await ScheduleWarnings.RelationsAsync(db, schedule.Id, ct);
        var calendar = works.Count != 0 && (works.Any(HasDate) || relations.Any(r => r.Predecessor.End.HasValue))
            ? await WorkingDays.LoadAsync(db, schedule.ConstructionId, ct) : null;
        var warnings = ScheduleWarnings.Read(schedule.Id, works, relations, calendar);
        var byWork = warnings.ToLookup(w => w.WorkId);
        var labels = await LabelsAsync(positions.Values.Select(p => p.UnitId), assignees.Select(a => a.EmployeeId), ct);
        return new(schedule.Id, schedule.ConstructionId, schedule.RevisionId, schedule.Name, db.VersionOf(schedule),
            new ScheduleSkipped(schedule.Unmapped), IsDistributionChanged(schedule, source)
                ? [ScheduleMarks.ChangedDistribution] : [],
            works.Select(w => Work(w, positions[w.PlanItemId], [.. employeeIds[w.Id]], labels, calendar, [.. byWork[w.Id]])).ToList(),
            relations.Select(r => r.View).ToList(), warnings);
    }

    internal static bool IsDistributionChanged(Schedule schedule, ScheduleSource? source) =>
        source is not null && source.Fingerprint != schedule.DistributionFingerprint;

    internal async Task<ScheduleWorkDto> ReadWorkAsync(
        Schedule schedule, ScheduleWork work, PlanItemKey item, IReadOnlyList<Guid> employeeIds, CancellationToken ct)
    {
        var calendar = work.Start.HasValue && work.End.HasValue
            ? await WorkingDays.LoadAsync(db, schedule.ConstructionId, ct) : null;
        var labels = await LabelsAsync([item.UnitId], employeeIds, ct);
        return Work(work, item, employeeIds, labels, calendar);
    }

    private static bool HasDate(ScheduleWork work) => work.Start.HasValue || work.End.HasValue;

    private ScheduleWorkDto Work(ScheduleWork work, PlanItemKey item, IReadOnlyList<Guid> employeeIds,
        WorkLabels labels, WorkingDays? calendar,
        IReadOnlyList<ScheduleWarning>? warnings = null) =>
        new(work.Id, work.PlanItemId, item.WorkTypeId, item.UnitId, work.SourceRowId, work.SectionId, work.Name,
            work.Volume, work.Start, work.End, employeeIds, work.SourceRowId is null, db.VersionOf(work),
            work.Start is { } start && work.End is { } end ? calendar?.WorkingDaysBetween(start, end).Value : null,
            labels.Units.GetValueOrDefault(item.UnitId),
            employeeIds.Where(labels.Assignees.ContainsKey).Select(id => labels.Assignees[id]).ToArray(), warnings);

    private sealed record WorkLabels(
        Dictionary<Guid, string> Units, Dictionary<Guid, ScheduleAssigneeDto> Assignees);

    private async Task<WorkLabels> LabelsAsync(IEnumerable<Guid> unitIds, IEnumerable<Guid> employeeIds, CancellationToken ct)
    {
        var unitKeys = unitIds.Distinct().ToArray();
        var employeeKeys = employeeIds.Distinct().ToArray();
        var knownUnits = unitKeys.Length == 0 ? [] : await units.GetAsync(unitKeys, ct) ?? [];
        var knownEmployees = employeeKeys.Length == 0 ? [] : await employees.GetAsync(employeeKeys, ct) ?? [];
        return new(knownUnits.ToDictionary(u => u.Id, u => u.Name),
            knownEmployees.ToDictionary(e => e.Id, e => new ScheduleAssigneeDto(e.Id, e.FullName, e.Archived)));
    }
}
