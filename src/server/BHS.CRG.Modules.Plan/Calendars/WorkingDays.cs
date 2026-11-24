using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Calendars;

public sealed record CalendarGap(int Year)
{
    internal const string MissingCode = "calendar-missing";

    public string Code => MissingCode;
    public string Text => $"Календарь компании на {Year} год не заполнен.";
}

public sealed record CalendarCalculation<T>(T Value, IReadOnlyList<CalendarGap> Gaps);

/// <summary>Снимок календаря одной стройки для расчётов графика. На входе календарные даты стройки.</summary>
public sealed class WorkingDays
{
    private readonly Dictionary<int, CalendarYear> _years;
    private readonly Dictionary<DateOnly, CalendarDayKind> _companyDays;
    private readonly Dictionary<DateOnly, CalendarDayKind> _siteDays;
    private readonly int? _siteWeek;

    private WorkingDays(List<CalendarYear> years, ConstructionCalendar? site)
    {
        _years = years.ToDictionary(y => y.Year);
        _companyDays = years.SelectMany(y => y.Days).ToDictionary(d => d.Date, d => d.Kind);
        _siteDays = site?.Days.ToDictionary(d => d.Date, d => d.Kind) ?? [];
        _siteWeek = site?.WorkingWeek;
        Shifts = site?.Shifts ?? 1;
    }

    /// <summary>Используется подсказкой длительности. На выбор рабочих дат не влияет.</summary>
    public int Shifts { get; }

    public static async Task<WorkingDays> LoadAsync(PlanDbContext db, Guid constructionId, CancellationToken ct = default)
    {
        var years = await db.Set<CalendarYear>().AsNoTracking().Include(y => y.Days).ToListAsync(ct);
        var site = await db.Set<ConstructionCalendar>().AsNoTracking().Include(c => c.Days)
            .SingleOrDefaultAsync(c => c.ConstructionId == constructionId, ct);
        return new WorkingDays(years, site);
    }

    public CalendarCalculation<bool> IsWorking(DateOnly date) =>
        new(IsWorkingDay(date), GapsBetween(date, date));

    public CalendarCalculation<decimal> HoursPerShift(DateOnly date)
    {
        var hours = _years.GetValueOrDefault(date.Year)?.HoursPerShift ?? 8;
        return new(EffectiveKind(date) switch
        {
            CalendarDayKind.Holiday => 0,
            CalendarDayKind.Short => Math.Max(0, hours - 1),
            _ => hours,
        }, GapsBetween(date, date));
    }

    /// <summary>Дни считаются со следующей или предыдущей даты. Нулевой шаг сохраняет дату.</summary>
    public CalendarCalculation<DateOnly> AddWorkingDays(DateOnly date, int offset)
    {
        var day = date;
        var step = Math.Sign(offset);
        var remaining = Math.Abs((long)offset);
        var available = step < 0 ? day.DayNumber : DateOnly.MaxValue.DayNumber - day.DayNumber;
        while (remaining > 0)
        {
            if (remaining > available)
                throw new InvalidRequestException("Смещение по рабочим дням выходит за пределы календарных дат.");
            day = day.AddDays(step);
            available--;
            if (IsWorkingDay(day)) remaining--;
        }
        return new(day, GapsBetween(date, day));
    }

    /// <summary>Длительность включает обе границы. Работа в один рабочий день имеет длительность 1.</summary>
    public CalendarCalculation<int> WorkingDaysBetween(DateOnly start, DateOnly end)
    {
        if (start > end) throw new InvalidRequestException("Дата окончания раньше даты начала.");
        var days = 0;
        for (var number = start.DayNumber; number <= end.DayNumber; number++)
            if (IsWorkingDay(DateOnly.FromDayNumber(number))) days++;

        return new(days, GapsBetween(start, end));
    }

    private bool IsWorkingDay(DateOnly date) => EffectiveKind(date) != CalendarDayKind.Holiday;

    private CalendarDayKind EffectiveKind(DateOnly date)
    {
        if (_siteDays.TryGetValue(date, out var kind) || _companyDays.TryGetValue(date, out kind))
            return kind;

        var week = _siteWeek ?? _years.GetValueOrDefault(date.Year)?.WorkingWeek ?? CalendarWeek.FiveDays;
        return CalendarWeek.Contains(week, date) ? CalendarDayKind.Working : CalendarDayKind.Holiday;
    }

    internal IReadOnlyList<CalendarGap> GapsBetween(DateOnly first, DateOnly second)
    {
        var min = Math.Min(first.Year, second.Year);
        var max = Math.Max(first.Year, second.Year);
        return Enumerable.Range(min, max - min + 1)
            .Where(year => !_years.ContainsKey(year)).Select(year => new CalendarGap(year)).ToArray();
    }
}
