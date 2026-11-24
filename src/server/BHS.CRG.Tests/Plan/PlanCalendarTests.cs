using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;

namespace BHS.CRG.Tests.Plan;

[Collection(PlanOnlyCollection.Name)]
public class PlanCalendarTests(PlanOnlyHost host)
{
    [Fact]
    public async Task Предпросмотр_подлинного_файла_не_пишет_и_применяется_только_к_прочитанной_версии()
    {
        using var editor = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.CalendarEdit);
        using var initialResponse = await editor.GetAsync(CalendarRequests.Company(2027));
        var empty = await CalendarRequests.ReadAsync(initialResponse);
        Assert.False(empty.GetProperty("configured").GetBoolean());
        Assert.Equal("0", CalendarRequests.Version(empty));
        Assert.Equal("calendar-missing", Assert.Single(empty.GetProperty("gaps").EnumerateArray()).GetProperty("code").GetString());

        var upload = CalendarRequests.Sample(2027);
        var preview = await CalendarRequests.PreviewAsync(editor, 2027, upload);
        Assert.Equal("0", CalendarRequests.Version(preview));
        var changes = preview.GetProperty("changes").EnumerateArray().ToDictionary(d => d.GetProperty("date").GetString()!);
        Assert.False(changes["2027-02-20"].GetProperty("wasWorking").GetBoolean());
        Assert.True(changes["2027-02-20"].GetProperty("isWorking").GetBoolean());
        Assert.Equal("short", changes["2027-02-20"].GetProperty("kind").GetString());
        Assert.False(changes["2027-02-22"].GetProperty("isWorking").GetBoolean());
        Assert.Contains("2027-06-12", changes.Keys); // праздник в субботу должен сохраниться для шестидневки
        Assert.DoesNotContain("2027-06-19", changes.Keys);
        Assert.Equal(empty.GetRawText(), (await editor.GetFromJsonAsync<JsonElement>(
            CalendarRequests.Company(2027))).GetRawText());

        var manual = await CalendarRequests.SaveCompanyAsync(editor, 2027, "0", new { date = "2027-02-10", kind = "holiday" });
        using var stale = await CalendarRequests.PutAsync(editor, CalendarRequests.Company(2027), "0", preview);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var fresh = await CalendarRequests.PreviewAsync(editor, 2027, upload);
        Assert.Equal(CalendarRequests.Version(manual), CalendarRequests.Version(fresh));
        var removal = Assert.Single(fresh.GetProperty("changes").EnumerateArray(),
            d => d.GetProperty("date").GetString() == "2027-02-10");
        Assert.True(removal.GetProperty("isWorking").GetBoolean());

        using var appliedResponse = await CalendarRequests.PutAsync(
            editor, CalendarRequests.Company(2027), CalendarRequests.Version(fresh), fresh);
        var applied = await CalendarRequests.ReadAsync(appliedResponse);
        Assert.True(applied.GetProperty("configured").GetBoolean());
        Assert.Equal("2027.ics", applied.GetProperty("importFileName").GetString());
        Assert.NotEqual(CalendarRequests.Version(fresh), CalendarRequests.Version(applied));
        Assert.Equal(fresh.GetProperty("days").GetRawText(), applied.GetProperty("days").GetRawText());

        // Правка дня после импорта сохраняет источник и дату последней загрузки.
        var edited = await CalendarRequests.SaveCompanyAsync(editor, 2027, CalendarRequests.Version(applied),
            new { date = "2027-02-10", kind = "working" });
        Assert.Equal(applied.GetProperty("importedAt"), edited.GetProperty("importedAt"), JsonElement.DeepEquals);
    }

    [Fact]
    public async Task Испорченная_строка_и_файл_чужого_года_получают_номер_строки()
    {
        using var editor = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.CalendarEdit);
        var sample = Encoding.UTF8.GetString(CalendarRequests.Sample(2027));
        var malformed = sample.Replace("DTSTART;VALUE=DATE:20270101", "DTSTART;VALUE=DATE:20270199", StringComparison.Ordinal);
        var physicalLine = 1 + sample[..sample.IndexOf("DTSTART;VALUE=DATE", StringComparison.Ordinal)].Count(c => c == '\n');
        using var broken = await CalendarRequests.PostFileAsync(editor, 2027, Encoding.UTF8.GetBytes(malformed));
        using var wrongYear = await CalendarRequests.PostFileAsync(editor, 2098, CalendarRequests.Sample(2027));
        foreach (var response in new[] { broken, wrongYear })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains($"строка {physicalLine}", error.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task Календарь_компании_за_правом_а_правки_годов_включая_первую_за_версией()
    {
        using var manager = await AccessProbe.SignInAsync(host, "ProjectManager", "plan18");
        using var executive = await AccessProbe.SignInAsync(host, "Executive", "plan18");
        using var editor = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.CalendarEdit);
        const int year = 2097;
        var url = CalendarRequests.Company(year);
        var body = new { workingWeek = 31, hoursPerShift = 8, days = Array.Empty<object>() };
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await executive.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CalendarRequests.PutAsync(manager, url, "0", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CalendarRequests.PutAsync(executive, url, "0", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CalendarRequests.PostFileAsync(manager, year, CalendarRequests.Sample(2027))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CalendarRequests.PutAsync(editor, url, null, body)).StatusCode);

        var first = await CalendarRequests.SaveCompanyAsync(editor, year, "0");
        var version = CalendarRequests.Version(first);
        Assert.Equal(HttpStatusCode.BadRequest, (await CalendarRequests.PutAsync(editor, url, null, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await CalendarRequests.PutAsync(editor, url, "0", body)).StatusCode);

        var monday = new { workingWeek = 31, hoursPerShift = 8, days = new[] { new { date = "2097-02-03", kind = "holiday" } } };
        var tuesday = new { workingWeek = 31, hoursPerShift = 8, days = new[] { new { date = "2097-02-04", kind = "short" } } };
        var races = await Task.WhenAll(CalendarRequests.PutAsync(editor, url, version, monday),
            CalendarRequests.PutAsync(editor, url, version, tuesday));
        var won = Assert.Single(races, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(races, response => response.StatusCode == HttpStatusCode.Conflict);
        var saved = await CalendarRequests.ReadAsync(won);
        Assert.NotEqual(version, CalendarRequests.Version(saved));
        var persisted = await editor.GetFromJsonAsync<JsonElement>(url);
        Assert.Equal(Assert.Single(saved.GetProperty("days").EnumerateArray()).GetRawText(),
            Assert.Single(persisted.GetProperty("days").EnumerateArray()).GetRawText());
    }

    [Fact]
    public async Task Календарь_стройки_держит_ссылку_и_изолирован_от_другой_стройки()
    {
        var site = await EstimateSeed.SiteAsync(host, "Стройка со своим календарём");
        var other = await EstimateSeed.SiteAsync(host, "Другая стройка");
        using var manager = await AccessProbe.SignInAsync(host, "ProjectManager", "plan18");
        using var editor = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.CalendarEdit);
        using var admin = await AccessProbe.SignInAsync(host, SystemRoles.Admin, "plan18");
        var url = CalendarRequests.Construction(site.Construction);
        var body = new { workingWeek = 63, shifts = 3, days = new[] { new { date = "2027-06-12", kind = "working" } } };
        Assert.Equal(HttpStatusCode.OK, (await editor.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CalendarRequests.PutAsync(editor, url, "0", body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CalendarRequests.PutAsync(manager, url, null, body)).StatusCode);
        var saved = await CalendarRequests.ReadAsync(await CalendarRequests.PutAsync(manager, url, "0", body));
        Assert.Equal(3, saved.GetProperty("shifts").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await CalendarRequests.PutAsync(manager, url, "0", body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync(CalendarRequests.Construction(Guid.NewGuid()))).StatusCode);
        var inherited = await manager.GetFromJsonAsync<JsonElement>(CalendarRequests.Construction(other.Construction));
        Assert.Equal("0", CalendarRequests.Version(inherited));
        Assert.Equal(1, inherited.GetProperty("shifts").GetInt32());
        Assert.Empty(inherited.GetProperty("days").EnumerateArray());
        using var refused = await admin.DeleteAsync($"/api/constructions/{site.Construction}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("настройки календаря этой стройки", (await refused.Content.ReadFromJsonAsync<JsonElement>()).ToString());
    }

    [Fact]
    public async Task Нерабочие_дни_учитывают_неделю_стройки_праздники_переносы_и_исключения()
    {
        const int year = 2162;
        var site = await EstimateSeed.SiteAsync(host, "Стройка с диаграммой");
        using var companyEditor = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.CalendarEdit);
        using var manager = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.ScheduleRead, PlanModule.ScheduleEdit);
        using var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.ScheduleRead);
        await CalendarRequests.SaveCompanyAsync(companyEditor, year, "0",
            new { date = "2162-06-05", kind = "holiday" }, new { date = "2162-06-06", kind = "short" });
        var url = CalendarRequests.Construction(site.Construction);
        var extraDayOff = new { date = "2162-06-07", kind = "holiday" };
        var settings = new { workingWeek = 63, shifts = 2, days = new[] { extraDayOff } };
        using var firstWrite = await CalendarRequests.PutAsync(manager, url, "0", settings);
        var saved = await CalendarRequests.ReadAsync(firstWrite);
        var range = CalendarRequests.NonWorkingDays(site.Construction, "2162-06-04", "2162-06-12");
        var first = await reader.GetFromJsonAsync<JsonElement>(range);
        Assert.Equal(new[] { "2162-06-05", "2162-06-07" }, Dates(first));
        Assert.Empty(first.GetProperty("gaps").EnumerateArray());
        using var secondWrite = await CalendarRequests.PutAsync(manager, url, CalendarRequests.Version(saved),
            settings with { days = [extraDayOff, new { date = "2162-06-05", kind = "working" }] });
        await CalendarRequests.ReadAsync(secondWrite);
        Assert.Equal(new[] { "2162-06-07" }, Dates(await reader.GetFromJsonAsync<JsonElement>(range)));

        using var denied = await companyEditor.GetAsync(range);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Нерабочие_дни_называют_пробел_и_проверяют_диапазон_и_стройку()
    {
        var site = await EstimateSeed.SiteAsync(host, "Стройка без года календаря");
        using var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.ScheduleRead);
        var missing = await reader.GetFromJsonAsync<JsonElement>(
            CalendarRequests.NonWorkingDays(site.Construction, "2163-01-01", "2163-01-07"));
        var gap = Assert.Single(missing.GetProperty("gaps").EnumerateArray());
        Assert.Equal("calendar-missing", gap.GetProperty("code").GetString());
        Assert.Equal(2163, gap.GetProperty("year").GetInt32());
        Assert.Equal(2, Dates(missing).Length);

        foreach (var (from, to) in new[] { ("2163-01-07", "2163-01-01"), ("2163-01-01", "2164-01-02") })
        {
            using var response = await reader.GetAsync(CalendarRequests.NonWorkingDays(site.Construction, from, to));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var unknown = await reader.GetAsync(CalendarRequests.NonWorkingDays(Guid.NewGuid(), "2163-01-01", "2163-01-07"));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    private static string?[] Dates(JsonElement result) =>
        result.GetProperty("nonWorkingDays").EnumerateArray().Select(date => date.GetString()).ToArray();
}

[Collection("Integration")]
public class PlanCalendarWithoutModuleTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task Нерабочие_дни_за_воротами_модуля()
    {
        using var client = fixture.CreateClient();
        using var response = await client.GetAsync(CalendarRequests.NonWorkingDays(Guid.NewGuid(), "2163-01-01", "2163-01-07"));
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Equal("plan", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("module").GetString());
    }
}
