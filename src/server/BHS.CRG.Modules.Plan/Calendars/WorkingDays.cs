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
    private readonly Dictionary<DateOnly, CalendarDayKind> _exceptions;
    private readonly int? _siteWeek;
    private readonly WorkingDayCount _workingDayCount;

    private WorkingDays(List<CalendarYear> years, ConstructionCalendar? site)
    {
        _years = years.ToDictionary(y => y.Year);
        _exceptions = years.SelectMany(y => y.Days).ToDictionary(d => d.Date, d => d.Kind);
        if (site is not null)
            foreach (var day in site.Days)
                _exceptions[day.Date] = day.Kind;
        _siteWeek = site?.WorkingWeek;
        _workingDayCount = new WorkingDayCount(_years, _exceptions, _siteWeek);
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
        var day = ShiftWorkingDays(date, offset)
            ?? throw new InvalidRequestException("Смещение по рабочим дням выходит за пределы календарных дат.");
        return new(day, GapsBetween(date, day));
    }

    /// <summary>Сдвиг без списка пропусков. Граница находится по числу рабочих дней в диапазоне.</summary>
    internal DateOnly? ShiftWorkingDays(DateOnly date, int offset)
    {
        if (offset == 0) return date;
        var origin = date.DayNumber;
        var step = Math.Sign(offset);
        var remaining = Math.Abs((long)offset);
        var available = step < 0 ? origin : DateOnly.MaxValue.DayNumber - origin;
        if (remaining > available) return null;

        // Близкие даты требуют счёта только по ближайшим неделям. Дальнюю границу ищем удвоением.
        var low = (int)remaining;
        var high = low;
        while (CountTo(high) < remaining)
        {
            if (high == available) return null;
            low = high + 1;
            high = Math.Min(available, high * 2);
        }
        // Первый диапазон с нужным счётом заканчивается рабочим днём.
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (CountTo(middle) < remaining) low = middle + 1;
            else high = middle;
        }
        return DateOnly.FromDayNumber(origin + step * low);

        int CountTo(int distance) => step < 0
            ? _workingDayCount.Between(origin - distance, origin - 1)
            : _workingDayCount.Between(origin + 1, origin + distance);
    }

    /// <summary>Длительность включает обе границы. Работа в один рабочий день имеет длительность 1.</summary>
    public CalendarCalculation<int> WorkingDaysBetween(DateOnly start, DateOnly end)
    {
        if (start > end) throw new InvalidRequestException("Дата окончания раньше даты начала.");
        return new(_workingDayCount.Between(start.DayNumber, end.DayNumber), GapsBetween(start, end));
    }

    internal bool IsWorkingDay(DateOnly date) => EffectiveKind(date) != CalendarDayKind.Holiday;

    private CalendarDayKind EffectiveKind(DateOnly date)
    {
        if (_exceptions.TryGetValue(date, out var kind))
            return kind;

        var week = _siteWeek ?? _years.GetValueOrDefault(date.Year)?.WorkingWeek ?? CalendarWeek.FiveDays;
        return CalendarWeek.Contains(week, date) ? CalendarDayKind.Working : CalendarDayKind.Holiday;
    }

    internal IEnumerable<CalendarGap> GapsInYears(int first, int last) =>
        Enumerable.Range(first, last - first + 1)
            .Where(year => !_years.ContainsKey(year)).Select(year => new CalendarGap(year));

    private IReadOnlyList<CalendarGap> GapsBetween(DateOnly first, DateOnly second)
    {
        var min = Math.Min(first.Year, second.Year);
        var max = Math.Max(first.Year, second.Year);
        return GapsInYears(min, max).ToArray();
    }
}
