using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Endpoints;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Calendars;

internal sealed class CalendarStore(PlanDbContext db, IModuleConstructions constructions, IModuleClock clock)
{
    private IQueryable<CalendarYear> Years => db.Set<CalendarYear>().Include(c => c.Days);
    private IQueryable<ConstructionCalendar> Sites => db.Set<ConstructionCalendar>().Include(c => c.Days);

    public async Task<CompanyCalendarDto> CompanyAsync(int year, CancellationToken ct)
    {
        CalendarViews.EnsureYear(year);
        var saved = await Years.SingleOrDefaultAsync(c => c.Year == year, ct);
        return CalendarViews.Company(saved ?? new(year), saved is null ? CalendarViews.MissingVersion : db.VersionOf(saved));
    }

    public async Task<ConstructionCalendarDto> ConstructionAsync(Guid id, CancellationToken ct)
    {
        var site = await FindSiteAsync(id, ct);
        var saved = await Sites.SingleOrDefaultAsync(c => c.ConstructionId == id, ct);
        return CalendarViews.Construction(saved ?? new(id), site.TimeZoneId,
            saved is null ? CalendarViews.MissingVersion : db.VersionOf(saved));
    }

    public async Task<CompanyCalendarDto> SaveCompanyAsync(
        int year, CompanyCalendarInput input, HttpRequest request, CancellationToken ct)
    {
        CalendarViews.EnsureYear(year);
        var saved = await Years.SingleOrDefaultAsync(c => c.Year == year, ct);
        EstimateEndpoints.EnsureSeen(request, saved is null ? CalendarViews.MissingVersion : db.VersionOf(saved),
            "Год", "календарь компании");

        var week = input.WorkingWeek ?? throw new InvalidRequestException("Укажите рабочие дни недели («workingWeek»).");
        CalendarViews.EnsureWeek(week);
        if (input.HoursPerShift is not { } hours || hours <= 0 || hours > 24 || decimal.Round(hours, 2) != hours)
            throw new InvalidRequestException("Укажите длительность смены больше нуля и до 24 часов, до сотых.");

        var desired = CalendarViews.ReadDays(input.Days, year);
        var calendar = saved ?? new(year);
        calendar.WorkingWeek = week;
        calendar.HoursPerShift = hours;
        if (input.ImportFileName is not null)
        {
            calendar.ImportFileName = CleanFileName(input.ImportFileName);
            var importedAt = clock.UtcNow();
            calendar.ImportedAt = importedAt.AddTicks(-(importedAt.Ticks % TimeSpan.TicksPerMicrosecond));
        }

        foreach (var day in calendar.Days.ToArray())
        {
            if (desired.Remove(day.Date, out var kind)) day.Kind = kind;
            else calendar.Days.Remove(day);
        }
        foreach (var (date, kind) in desired)
            calendar.Days.Add(new(year, date, kind));

        if (saved is null) db.Add(calendar);
        // Даже если изменились только дочерние дни, UPDATE года проверяет xmin в той же транзакции.
        else db.Entry(calendar).Property(c => c.WorkingWeek).IsModified = true;
        await db.SaveChangesAsync(ct);
        return CalendarViews.Company(calendar, db.VersionOf(calendar));
    }

    public async Task<ConstructionCalendarDto> SaveConstructionAsync(
        Guid id, ConstructionCalendarInput input, HttpRequest request, CancellationToken ct)
    {
        var site = await FindSiteAsync(id, ct);
        var saved = await Sites.SingleOrDefaultAsync(c => c.ConstructionId == id, ct);
        EstimateEndpoints.EnsureSeen(request, saved is null ? CalendarViews.MissingVersion : db.VersionOf(saved),
            "Календарь стройки", "его");

        CalendarViews.EnsureWeek(input.WorkingWeek);
        if (input.Shifts is not (>= 1 and <= 3))
            throw new InvalidRequestException("Укажите одну, две или три смены.");

        var desired = CalendarViews.ReadDays(input.Days);
        var calendar = saved ?? new(id);
        calendar.WorkingWeek = input.WorkingWeek;
        calendar.Shifts = input.Shifts.Value;
        foreach (var day in calendar.Days.ToArray())
        {
            if (desired.Remove(day.Date, out var kind)) day.Kind = kind;
            else calendar.Days.Remove(day);
        }
        foreach (var (date, kind) in desired)
            calendar.Days.Add(new(id, date, kind));

        if (saved is null) db.Add(calendar);
        else db.Entry(calendar).Property(c => c.Shifts).IsModified = true;
        await db.SaveChangesAsync(ct);
        return CalendarViews.Construction(calendar, site.TimeZoneId, db.VersionOf(calendar));
    }

    internal static string CleanFileName(string fileName)
    {
        var name = fileName.Replace('\\', '/').Split('/')[^1].Trim();
        if (name.Length is 0 or > 255 || name.Any(char.IsControl))
            throw new InvalidRequestException("Укажите имя файла календаря длиной до 255 символов.");
        return name;
    }

    private async Task<ModuleConstruction> FindSiteAsync(Guid id, CancellationToken ct) =>
        (await constructions.ListAsync(ct)).FirstOrDefault(c => c.Id == id)
        ?? throw new NotFoundException("Стройка не найдена.");
}
