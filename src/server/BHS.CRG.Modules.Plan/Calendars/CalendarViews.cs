using BHS.CRG.Domain.Common;

namespace BHS.CRG.Modules.Plan.Calendars;

internal sealed record CalendarDayDto(DateOnly Date, string Kind);
internal sealed record CalendarDayInput(DateOnly? Date, string? Kind);
internal sealed record CompanyCalendarInput(
    int? WorkingWeek, decimal? HoursPerShift, IReadOnlyList<CalendarDayInput?>? Days, string? ImportFileName);
internal sealed record ConstructionCalendarInput(int? WorkingWeek, int? Shifts, IReadOnlyList<CalendarDayInput?>? Days);
internal sealed record CompanyCalendarDto(
    int Year, bool Configured, int WorkingWeek, decimal HoursPerShift, IReadOnlyList<CalendarDayDto> Days,
    string? ImportFileName, DateTimeOffset? ImportedAt, string Version, IReadOnlyList<CalendarGap> Gaps);
internal sealed record ConstructionCalendarDto(
    Guid ConstructionId, string TimeZoneId, int? WorkingWeek, int Shifts, IReadOnlyList<CalendarDayDto> Days, string Version);

internal static class CalendarViews
{
    // Версия отсутствующей записи. Первое сохранение тоже называет прочитанную версию.
    public const string MissingVersion = "0";

    public static CompanyCalendarDto Company(CalendarYear calendar, string version) => new(
        calendar.Year, version != MissingVersion, calendar.WorkingWeek, calendar.HoursPerShift,
        Days(calendar.Days.Select(d => (d.Date, d.Kind))), calendar.ImportFileName, calendar.ImportedAt,
        version, version == MissingVersion ? [new(calendar.Year)] : []);

    public static ConstructionCalendarDto Construction(ConstructionCalendar calendar, string timeZoneId, string version) => new(
        calendar.ConstructionId, timeZoneId, calendar.WorkingWeek, calendar.Shifts,
        Days(calendar.Days.Select(d => (d.Date, d.Kind))), version);

    public static CalendarDayDto[] Days(IEnumerable<(DateOnly Date, CalendarDayKind Kind)> days) =>
        days.OrderBy(d => d.Date).Select(d => new CalendarDayDto(d.Date, Kind(d.Kind))).ToArray();

    public static string Kind(CalendarDayKind kind) => kind switch
    {
        CalendarDayKind.Holiday => "holiday",
        CalendarDayKind.Working => "working",
        CalendarDayKind.Short => "short",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static Dictionary<DateOnly, CalendarDayKind> ReadDays(IReadOnlyList<CalendarDayInput?>? days, int? year = null)
    {
        if (days is null) throw new InvalidRequestException("Передайте список исключений календаря («days»).");

        var result = new Dictionary<DateOnly, CalendarDayKind>();
        foreach (var day in days)
        {
            if (day?.Date is not { } date || (year.HasValue && date.Year != year.Value))
                throw new InvalidRequestException("Укажите дату исключения в пределах редактируемого календаря.");
            var kind = day.Kind switch
            {
                "holiday" => CalendarDayKind.Holiday,
                "working" => CalendarDayKind.Working,
                "short" => CalendarDayKind.Short,
                _ => throw new InvalidRequestException($"У исключения {date:dd.MM.yyyy} неизвестный вид."),
            };
            if (!result.TryAdd(date, kind))
                throw new InvalidRequestException($"В календаре два исключения на дату {date:dd.MM.yyyy}.");
        }
        return result;
    }

    public static void EnsureWeek(int? week)
    {
        if (week is < 1 or > CalendarWeek.AllDays)
            throw new InvalidRequestException("Выберите хотя бы один рабочий день недели.");
    }

    public static void EnsureYear(int year)
    {
        if (year is < 1 or > 9999) throw new InvalidRequestException("Укажите год от 1 до 9999.");
    }
}
