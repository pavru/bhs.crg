namespace BHS.CRG.Modules.Plan.Calendars;

internal enum CalendarDayKind
{
    Holiday,
    Working,
    Short,
}

internal sealed class CalendarYear(int year)
{
    public int Year { get; } = year;
    public int WorkingWeek { get; set; } = CalendarWeek.FiveDays;
    public decimal HoursPerShift { get; set; } = 8;
    public string? ImportFileName { get; set; }
    public DateTimeOffset? ImportedAt { get; set; }
    public List<CalendarDay> Days { get; } = [];
}

internal sealed class CalendarDay(int year, DateOnly date, CalendarDayKind kind)
{
    public int Year { get; } = year;
    public DateOnly Date { get; } = date;
    public CalendarDayKind Kind { get; set; } = kind;
}

internal sealed class ConstructionCalendar(Guid constructionId)
{
    public Guid ConstructionId { get; } = constructionId;
    public int? WorkingWeek { get; set; }
    public int Shifts { get; set; } = 1;
    public List<ConstructionCalendarDay> Days { get; } = [];
}

internal sealed class ConstructionCalendarDay(Guid constructionId, DateOnly date, CalendarDayKind kind)
{
    public Guid ConstructionId { get; } = constructionId;
    public DateOnly Date { get; } = date;
    public CalendarDayKind Kind { get; set; } = kind;
}

internal static class CalendarWeek
{
    // Младший бит соответствует понедельнику, старший — воскресенью.
    public const int FiveDays = 0b0011111;
    public const int AllDays = 0b1111111;
    public static bool Contains(int week, DateOnly date) => (week & (1 << (((int)date.DayOfWeek + 6) % 7))) != 0;
}
