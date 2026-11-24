using System.Globalization;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Calendars;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Schedules;

internal sealed record ScheduleRelation(
    ScheduleLink Link, ScheduleWork Predecessor, ScheduleWork Successor, uint RowVersion)
{
    internal ScheduleLinkDto View => ScheduleLinkStore.View(Link, RowVersion.ToString(CultureInfo.InvariantCulture));
}

internal static class ScheduleWarnings
{
    internal static Task<List<ScheduleRelation>> RelationsAsync(PlanDbContext db, Guid scheduleId, CancellationToken ct) =>
        (from link in db.ScheduleLinks.AsNoTracking()
         join predecessor in db.ScheduleWorks.AsNoTracking() on link.PredecessorId equals predecessor.Id
         join successor in db.ScheduleWorks.AsNoTracking() on link.SuccessorId equals successor.Id
         where predecessor.ScheduleId == scheduleId || successor.ScheduleId == scheduleId
         orderby link.Id
         select new ScheduleRelation(link, predecessor, successor, EF.Property<uint>(link, PlanDbContext.RowVersion)))
        .ToListAsync(ct);

    internal static async Task<IReadOnlyList<ScheduleWarning>> ReadAsync(
        PlanDbContext db, Schedule schedule, IReadOnlyList<ScheduleWork> works,
        IReadOnlyList<ScheduleRelation> relations, CancellationToken ct)
    {
        if (works.Count == 0) return [];
        var warnings = new List<ScheduleWarning>();
        if (works.All(w => w.Start is null && w.End is null))
            return works.Select(w => NoDates(w.Id)).ToList();

        var calendar = await WorkingDays.LoadAsync(db, schedule.ConstructionId, ct);
        foreach (var work in works)
        {
            if (work.Start is null || work.End is null)
                warnings.Add(NoDates(work.Id));
            if (work.End is { } end && !calendar.IsWorking(end).Value)
                warnings.Add(new(ScheduleMarks.EndsOnDayOff, "Окончание работы приходится на выходной.", work.Id));
            if ((work.Start ?? work.End) is { } first)
                warnings.AddRange(calendar.GapsBetween(first, work.End ?? first)
                    .Select(gap => ScheduleMarks.MissingCalendar(work.Id, gap)));
        }

        var thresholds = new Dictionary<(DateOnly, int), CalendarCalculation<DateOnly>?>();
        foreach (var relation in relations)
        {
            if (relation.Predecessor.End is not { } end || relation.Successor.Start is not { } start) continue;
            var key = (end, relation.Link.Lag);
            if (!thresholds.TryGetValue(key, out var due))
                thresholds[key] = due = Threshold(calendar, end, relation.Link.Lag);
            var affected = new[] { relation.Predecessor, relation.Successor };
            foreach (var work in affected.Where(w => w.ScheduleId == schedule.Id))
            {
                if (due is not null)
                    warnings.AddRange(due.Gaps.Select(gap => ScheduleMarks.MissingCalendar(work.Id, gap, relation.Link.Id)));
                if (due is not null ? start < due.Value : relation.Link.Lag > 0)
                    warnings.Add(new(ScheduleMarks.StartsBeforePredecessor, Violation(relation, due?.Value),
                        work.Id, relation.Link.Id));
            }
        }
        return warnings.DistinctBy(w => (w.Code, w.WorkId, w.Code == CalendarGap.MissingCode ? null : w.LinkId, w.Year)).ToList();
    }

    private static ScheduleWarning NoDates(Guid id) =>
        new(ScheduleMarks.NoDates, "У работы не назначены все даты.", id);

    private static CalendarCalculation<DateOnly>? Threshold(WorkingDays calendar, DateOnly end, int lag)
    {
        try
        {
            return calendar.AddWorkingDays(end, lag);
        }
        catch (InvalidRequestException)
        {
            // Сдвиг за пределы DateOnly всегда позже (положительный) либо раньше (отрицательный) любой даты.
            return null;
        }
    }

    private static string Violation(ScheduleRelation relation, DateOnly? due) =>
        due is { } date
            ? $"«{relation.Successor.Name}»: начало раньше {date:dd.MM.yyyy} по связи с «{relation.Predecessor.Name}»."
            : $"«{relation.Successor.Name}»: задержка после «{relation.Predecessor.Name}» выходит за пределы календаря.";
}
