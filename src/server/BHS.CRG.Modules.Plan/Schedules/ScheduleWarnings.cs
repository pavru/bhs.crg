using System.Globalization;
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
    private sealed record GapRange(Guid WorkId, int First, int Last, Guid? LinkId = null)
    {
        internal static GapRange Between(Guid work, DateOnly a, DateOnly b, Guid? link = null) =>
            new(work, Math.Min(a.Year, b.Year), Math.Max(a.Year, b.Year), link);
    }

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
        var gapRanges = new List<GapRange>();
        foreach (var work in works)
        {
            if (work.Start is null || work.End is null)
                warnings.Add(NoDates(work.Id));
            if (work.End is { } end && !calendar.IsWorkingDay(end))
                warnings.Add(new(ScheduleMarks.EndsOnDayOff, "Окончание работы приходится на выходной.", work.Id));
            if ((work.Start ?? work.End) is { } first)
                gapRanges.Add(GapRange.Between(work.Id, first, work.End ?? first));
        }

        var thresholds = new Dictionary<(DateOnly, int), DateOnly?>();
        foreach (var relation in relations)
        {
            if (relation.Predecessor.End is not { } end || relation.Successor.Start is not { } start) continue;
            var key = (end, relation.Link.Lag);
            if (!thresholds.TryGetValue(key, out var due))
                thresholds[key] = due = calendar.ShiftWorkingDays(end, relation.Link.Lag);
            // Сдвиг за пределы DateOnly позже любой даты при положительной задержке.
            var early = due is { } threshold ? start < threshold : relation.Link.Lag > 0;
            var affected = new[] { relation.Predecessor, relation.Successor };
            foreach (var work in affected.Where(w => w.ScheduleId == schedule.Id))
            {
                if (due is { } date)
                    gapRanges.Add(GapRange.Between(work.Id, end, date, relation.Link.Id));
                if (early)
                    warnings.Add(new(ScheduleMarks.StartsBeforePredecessor, Violation(relation, due),
                        work.Id, relation.Link.Id));
            }
        }
        AddCalendarGaps(calendar, gapRanges, warnings);
        return warnings;
    }

    private static ScheduleWarning NoDates(Guid id) =>
        new(ScheduleMarks.NoDates, "У работы не назначены все даты.", id);

    private static void AddCalendarGaps(WorkingDays calendar, List<GapRange> ranges, List<ScheduleWarning> warnings)
    {
        foreach (var work in ranges.GroupBy(r => r.WorkId))
        {
            // Пересечения сроков и задержек обходятся один раз на работу, независимо от числа её связей.
            var coveredUntil = 0;
            foreach (var range in work.OrderBy(r => r.First))
            {
                var first = Math.Max(coveredUntil + 1, range.First);
                if (first > range.Last) continue;
                warnings.AddRange(calendar.GapsInYears(first, range.Last)
                    .Select(gap => ScheduleMarks.MissingCalendar(work.Key, gap, range.LinkId)));
                coveredUntil = range.Last;
            }
        }
    }

    private static string Violation(ScheduleRelation relation, DateOnly? due) =>
        due is { } date
            ? $"«{relation.Successor.Name}»: начало раньше {date:dd.MM.yyyy} по связи с «{relation.Predecessor.Name}»."
            : $"«{relation.Successor.Name}»: задержка после «{relation.Predecessor.Name}» выходит за пределы календаря.";
}
