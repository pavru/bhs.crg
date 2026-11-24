using System.Numerics;

namespace BHS.CRG.Modules.Plan.Calendars;

/// <summary>Считает рабочие дни полными неделями, учитывая годы со своей неделей и исключения по датам.</summary>
internal sealed class WorkingDayCount
{
    private sealed record YearWeek(int First, int Last, int Mask);

    private readonly int defaultWeek;
    private readonly YearWeek[] differentWeeks;
    private readonly int[] exceptionDays;
    private readonly int[] exceptionTotals;

    internal WorkingDayCount(IReadOnlyDictionary<int, CalendarYear> years,
        IReadOnlyDictionary<DateOnly, CalendarDayKind> exceptions, int? siteWeek)
    {
        defaultWeek = siteWeek ?? CalendarWeek.FiveDays;
        differentWeeks = siteWeek.HasValue ? [] : years.Values
            .Where(y => y.WorkingWeek != defaultWeek).OrderBy(y => y.Year)
            .Select(y => new YearWeek(new DateOnly(y.Year, 1, 1).DayNumber,
                new DateOnly(y.Year, 12, 31).DayNumber, y.WorkingWeek)).ToArray();

        // Префиксная сумма поправок позволяет учитывать исключения диапазона двумя поисками.
        exceptionDays = new int[exceptions.Count];
        exceptionTotals = new int[exceptions.Count + 1];
        var index = 0;
        foreach (var (date, kind) in exceptions.OrderBy(e => e.Key))
        {
            var week = siteWeek ?? years.GetValueOrDefault(date.Year)?.WorkingWeek ?? defaultWeek;
            var normal = CalendarWeek.Contains(week, date) ? 1 : 0;
            var actual = kind == CalendarDayKind.Holiday ? 0 : 1;
            exceptionDays[index] = date.DayNumber;
            exceptionTotals[index + 1] = exceptionTotals[index] + actual - normal;
            index++;
        }
    }

    internal int Between(int first, int last)
    {
        var result = CountingWeek(defaultWeek, first, last);
        foreach (var year in differentWeeks)
        {
            if (year.Last < first) continue;
            if (year.First > last) break;
            var start = Math.Max(first, year.First);
            var end = Math.Min(last, year.Last);
            result += CountingWeek(year.Mask, start, end) - CountingWeek(defaultWeek, start, end);
        }
        return result + ExceptionsBefore(last + 1) - ExceptionsBefore(first);
    }

    private int ExceptionsBefore(int day)
    {
        var index = Array.BinarySearch(exceptionDays, day);
        return exceptionTotals[index < 0 ? ~index : index];
    }

    private static int CountingWeek(int week, int first, int last)
    {
        var count = last - first + 1;
        var result = count / 7 * BitOperations.PopCount((uint)week);
        for (var i = 0; i < count % 7; i++)
            if (CalendarWeek.Contains(week, DateOnly.FromDayNumber(first + i))) result++;
        return result;
    }
}
