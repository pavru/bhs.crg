namespace BHS.CRG.Modules.Plan.Calendars;

internal sealed record CalendarImportChange(
    DateOnly Date, string? PreviousKind, string? Kind, bool WasWorking, bool IsWorking);

internal sealed record CalendarImportPreview(
    int Year, string Version, int WorkingWeek, decimal HoursPerShift, string ImportFileName,
    IReadOnlyList<CalendarDayDto> Days, IReadOnlyList<CalendarImportChange> Changes)
{
    public static CalendarImportPreview Compare(
        CompanyCalendarDto current, Dictionary<DateOnly, CalendarDayKind> proposed, string fileName)
    {
        var days = CalendarViews.Days(proposed.Select(d => (d.Key, d.Value)));
        var before = current.Days.ToDictionary(d => d.Date, d => d.Kind);
        var after = days.ToDictionary(d => d.Date, d => d.Kind);
        var changes = before.Keys.Union(after.Keys).OrderBy(date => date)
            .Where(date => before.GetValueOrDefault(date) != after.GetValueOrDefault(date))
            .Select(date =>
            {
                var previous = before.GetValueOrDefault(date);
                var next = after.GetValueOrDefault(date);
                var baseline = CalendarWeek.Contains(current.WorkingWeek, date);
                return new CalendarImportChange(date, previous, next, Working(previous, baseline), Working(next, baseline));
            }).ToArray();
        return new(current.Year, current.Version, current.WorkingWeek, current.HoursPerShift, fileName, days, changes);
    }

    private static bool Working(string? kind, bool baseline) => kind is null ? baseline : kind != "holiday";
}
