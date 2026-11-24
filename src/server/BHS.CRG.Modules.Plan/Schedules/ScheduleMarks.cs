using BHS.CRG.Modules.Plan.Calendars;
using BHS.CRG.Modules.Plan.Endpoints;

namespace BHS.CRG.Modules.Plan.Schedules;

internal static class ScheduleMarks
{
    internal const string DistributionChanged = "distribution-changed";
    internal const string StartsBeforePredecessor = "starts-before-predecessor";
    internal const string EndsOnDayOff = "ends-on-day-off";
    internal const string NoDates = "no-dates";

    internal static readonly ScheduleMark ChangedDistribution =
        new(DistributionChanged, "Распределение изменено после построения.");

    internal static ScheduleWarning MissingCalendar(Guid workId, CalendarGap gap, Guid? linkId = null) =>
        new(gap.Code, gap.Text, workId, linkId, gap.Year);
}
