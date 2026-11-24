using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Endpoints;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Schedules;

internal sealed class ScheduleViews(PlanDbContext db, IModulePlanItems items)
{
    internal const string DistributionChanged = "distribution-changed";

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
        var employees = assignees.ToLookup(a => a.WorkId, a => a.EmployeeId);
        var positions = (await items.GetAsync(works.Select(w => w.PlanItemId).Distinct().ToArray(), ct))
            .ToDictionary(p => p.Id, p => p.Key);
        return new(schedule.Id, schedule.ConstructionId, schedule.RevisionId, schedule.Name, db.VersionOf(schedule),
            new ScheduleSkipped(schedule.Unmapped), IsDistributionChanged(schedule, source)
                ? [new ScheduleMark(DistributionChanged, "Распределение изменено после построения.")]
                : [],
            works.Select(w => Work(w, positions[w.PlanItemId], [.. employees[w.Id]])).ToList());
    }

    internal static bool IsDistributionChanged(Schedule schedule, ScheduleSource? source) =>
        source is not null && source.Fingerprint != schedule.DistributionFingerprint;

    internal ScheduleWorkDto Work(ScheduleWork work, PlanItemKey item, IReadOnlyList<Guid> employees) =>
        new(work.Id, work.PlanItemId, item.WorkTypeId, item.UnitId, work.SourceRowId, work.SectionId, work.Name,
            work.Volume, work.Start, work.End, employees, work.SourceRowId is null, db.VersionOf(work));
}
