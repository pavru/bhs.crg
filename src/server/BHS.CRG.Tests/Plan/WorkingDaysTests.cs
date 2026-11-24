using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Calendars;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Plan;

[Collection(PlanOnlyCollection.Name)]
public class WorkingDaysTests(PlanOnlyHost host)
{
    [Fact]
    public async Task Длинная_задержка_в_обе_стороны_учитывает_неделю_года_и_исключения()
    {
        const int year = 2400;
        var january = new DateOnly(year, 1, 1);
        var monday = Enumerable.Range(0, 7).Select(january.AddDays).Single(d => d.DayOfWeek == DayOfWeek.Monday);
        using var editor = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.CalendarEdit);
        using var saved = await CalendarRequests.PutAsync(editor, CalendarRequests.Company(year), "0", new
        {
            workingWeek = 1,
            hoursPerShift = 8,
            days = new[] { new { date = monday, kind = "holiday" }, new { date = monday.AddDays(1), kind = "short" } },
        });
        await CalendarRequests.ReadAsync(saved);

        var site = (await EstimateSeed.SiteAsync(host)).Construction;
        using var scope = host.Services.CreateScope();
        var calendar = await WorkingDays.LoadAsync(scope.ServiceProvider.GetRequiredService<PlanDbContext>(), site);
        var start = new DateOnly(year - 1, 11, 1);
        var end = new DateOnly(year + 1, 5, 31);
        var workingDates = Enumerable.Range(start.DayNumber + 1, end.DayNumber - start.DayNumber - 1)
            .Select(DateOnly.FromDayNumber).Where(day => calendar.IsWorking(day).Value).ToArray();
        var forward = calendar.AddWorkingDays(start, workingDates.Length);
        var reverse = calendar.AddWorkingDays(end, -workingDates.Length);
        Assert.Equal(workingDates[^1], forward.Value);
        Assert.Equal(workingDates[0], reverse.Value);
        Assert.Equal(workingDates.Length, calendar.WorkingDaysBetween(start.AddDays(1), end.AddDays(-1)).Value);
        Assert.Equal(new[] { year - 1, year + 1 }, forward.Gaps.Select(g => g.Year));
        Assert.Equal(forward.Gaps, reverse.Gaps);
    }

    [Fact]
    public async Task Сдвиг_на_полмиллиона_рабочих_дней_и_пределы_представления_дат()
    {
        var site = (await EstimateSeed.SiteAsync(host)).Construction;
        using var scope = host.Services.CreateScope();
        var calendar = await WorkingDays.LoadAsync(scope.ServiceProvider.GetRequiredService<PlanDbContext>(), site);
        var january = new DateOnly(6000, 1, 1);
        var monday = Enumerable.Range(0, 7).Select(january.AddDays).Single(d => d.DayOfWeek == DayOfWeek.Monday);
        Assert.Equal(monday.AddDays(700_000), calendar.AddWorkingDays(monday, 500_000).Value);
        Assert.Equal(monday.AddDays(-700_000), calendar.AddWorkingDays(monday, -500_000).Value);
        Assert.Throws<InvalidRequestException>(() => calendar.AddWorkingDays(monday, int.MaxValue));
        Assert.Throws<InvalidRequestException>(() => calendar.AddWorkingDays(monday, int.MinValue));
    }

    [Fact]
    public async Task Майские_исключения_действуют_на_шестидневке_и_переопределяются_по_дате()
    {
        using var calendarEditor = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.CalendarEdit);
        var preview = await CalendarRequests.PreviewAsync(calendarEditor, 2026, CalendarRequests.Sample(2026));
        using var save = await CalendarRequests.PutAsync(calendarEditor, CalendarRequests.Company(2026),
            CalendarRequests.Version(preview), preview);
        Assert.True(save.IsSuccessStatusCode, await save.Content.ReadAsStringAsync());

        var site = (await EstimateSeed.SiteAsync(host)).Construction;
        using var manager = await AccessProbe.SignInAsync(host, "ProjectManager", "plan18");
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
        var inherited = await WorkingDays.LoadAsync(db, site);
        var friday = new DateOnly(2026, 5, 8);
        var holiday = new DateOnly(2026, 5, 9);
        var wednesday = new DateOnly(2026, 5, 13);
        Assert.Equal(wednesday, inherited.AddWorkingDays(friday, 2).Value);
        Assert.Equal(friday, inherited.AddWorkingDays(wednesday, -2).Value);
        Assert.Equal(3, inherited.WorkingDaysBetween(friday, wednesday).Value);
        Assert.Equal(1, inherited.WorkingDaysBetween(friday, friday).Value);
        Assert.Equal(7m, inherited.HoursPerShift(friday).Value);
        Assert.Empty(inherited.AddWorkingDays(friday, 2).Gaps);

        var first = await PutSiteAsync("0", shifts: 3, days: []);
        var sixDays = await WorkingDays.LoadAsync(db, site);
        Assert.Equal(3, sixDays.Shifts);
        Assert.True(sixDays.IsWorking(new DateOnly(2026, 5, 16)).Value);
        Assert.False(sixDays.IsWorking(holiday).Value);
        Assert.Equal(wednesday, sixDays.AddWorkingDays(friday, 2).Value);

        var exception = new { date = "2026-05-09", kind = "working" };
        var overridden = await PutSiteAsync(CalendarRequests.Version(first), shifts: 3, [exception]);
        var changed = await WorkingDays.LoadAsync(db, site);
        Assert.Equal(holiday, changed.AddWorkingDays(friday, 1).Value);
        Assert.Equal(8m, changed.HoursPerShift(holiday).Value);

        await PutSiteAsync(CalendarRequests.Version(overridden), shifts: 1, [exception]);
        var oneShift = await WorkingDays.LoadAsync(db, site);
        Assert.Equal(1, oneShift.Shifts);
        Assert.Equal(holiday, oneShift.AddWorkingDays(friday, 1).Value);

        async Task<JsonElement> PutSiteAsync(string version, int shifts, object[] days)
        {
            using var response = await CalendarRequests.PutAsync(manager, CalendarRequests.Construction(site), version,
                new { workingWeek = 63, shifts, days });
            return await CalendarRequests.ReadAsync(response);
        }
    }

    [Fact]
    public async Task На_границе_года_пробел_передаётся_в_обе_стороны()
    {
        const int filledYear = 2099;
        using var editor = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.CalendarEdit);
        var last = new DateOnly(filledYear, 12, 31);
        await CalendarRequests.SaveCompanyAsync(editor, filledYear, "0", new { date = last, kind = "working" });
        var site = (await EstimateSeed.SiteAsync(host)).Construction;
        var otherSite = (await EstimateSeed.SiteAsync(host)).Construction;
        var seed = new WorkTypeSeed(host);
        await seed.SendAsync(new SetConstructionTimeZoneCommand(site, "Asia/Vladivostok"));
        await seed.SendAsync(new SetConstructionTimeZoneCommand(otherSite, "Europe/Moscow"));
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
        var calendar = await WorkingDays.LoadAsync(db, site);
        var next = calendar.AddWorkingDays(last, 1);
        Assert.Equal(new DateOnly(2100, 1, 1), next.Value); // пятница
        Assert.Equal(2100, Assert.Single(next.Gaps).Year);
        var previous = calendar.AddWorkingDays(next.Value, -1);
        Assert.Equal(last, previous.Value);
        Assert.Equal("calendar-missing", Assert.Single(previous.Gaps).Code);
        Assert.Equal(2, calendar.WorkingDaysBetween(last, next.Value).Value);
        Assert.Equal(2100, Assert.Single(calendar.AddWorkingDays(next.Value, 0).Gaps).Year);
        var otherCalendar = await WorkingDays.LoadAsync(db, otherSite);
        Assert.Equal(next.Value, otherCalendar.AddWorkingDays(last, 1).Value);
        var siteView = await editor.GetFromJsonAsync<JsonElement>(CalendarRequests.Construction(site));
        var otherView = await editor.GetFromJsonAsync<JsonElement>(CalendarRequests.Construction(otherSite));
        Assert.Equal("Asia/Vladivostok", siteView.GetProperty("timeZoneId").GetString());
        Assert.Equal("Europe/Moscow", otherView.GetProperty("timeZoneId").GetString());
    }
}
