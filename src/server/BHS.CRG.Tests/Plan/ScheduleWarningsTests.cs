using System.Net;
using System.Text.Json;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;
using static BHS.CRG.Tests.Plan.OfferDerivativeScenario;
using static BHS.CRG.Tests.Plan.ScheduleScenario;

namespace BHS.CRG.Tests.Plan;

[Collection(PlanOnlyCollection.Name)]
public class ScheduleWarningsTests(PlanOnlyHost host)
{
    [Theory]
    [InlineData(int.MaxValue, "2026-12-04")]
    [InlineData(int.MinValue, null)]
    public async Task Задержка_вне_диапазона_дат_видна_у_обеих_работ(int lag, string? start)
    {
        var scene = await CreateAsync(host);
        var first = await scene.BuildAsync();
        var second = await scene.BuildAsync(name: "Общестрой");
        var predecessor = await scene.ManualAsync(first, "2026-12-01", "2026-12-03");
        var successor = await scene.ManualAsync(second, start, start);
        var link = await ReadAsync(scene.LinkAsync(second, predecessor, successor, lag: lag), HttpStatusCode.Created);

        foreach (var (schedule, work) in new[] { (first, predecessor), (second, successor) })
        {
            var view = await ReadAsync(scene.Editor.GetAsync(ScheduleUrl(schedule)));
            var warning = Assert.Single(Warnings(view), w => Code(w) == "lag-out-of-range");
            Assert.Equal(Id(work), GuidOrNull(warning, "workId"));
            Assert.Equal(Id(link), GuidOrNull(warning, "linkId"));
            Assert.DoesNotContain(Warnings(view), w => Code(w) == "starts-before-predecessor");
            var shown = Assert.Single(Works(view));
            Assert.Contains(Warnings(shown), w => Code(w) == "lag-out-of-range");
            Assert.Equal(work.GetProperty("start").GetString(), shown.GetProperty("start").GetString());
            Assert.Equal(Version(work), Version(shown));
        }
    }

    [Fact]
    public async Task Пересекающиеся_длинные_задержки_называют_каждый_пробел_один_раз()
    {
        var scene = await CreateAsync(host);
        var schedule = await scene.BuildAsync();
        var first = await scene.ManualAsync(schedule, "2150-01-01", "2150-01-01");
        var second = await scene.ManualAsync(schedule, "2150-02-01", "2150-02-01");
        var target = await scene.ManualAsync(schedule, "2151-06-01", "2151-06-02");
        await ReadAsync(scene.LinkAsync(schedule, first, target, lag: 600), HttpStatusCode.Created);
        await ReadAsync(scene.LinkAsync(schedule, second, target, lag: 580), HttpStatusCode.Created);

        var view = await ReadAsync(scene.Editor.GetAsync(ScheduleUrl(schedule)));
        var work = Assert.Single(Works(view), w => Id(w) == Id(target));
        Assert.Equal(new[] { 2150, 2151, 2152 }, Warnings(work).Where(w => Code(w) == "calendar-missing")
            .Select(w => w.GetProperty("year").GetInt32()));
        Assert.Equal(2, Warnings(work).Count(w => Code(w) == "starts-before-predecessor"));
    }

    [Fact]
    public async Task Отрицательная_задержка_учитывает_субботу_стройки_и_праздник_компании()
    {
        var scene = await CreateAsync(host);
        using var calendarEditor = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.CalendarEdit);
        var baseline = await ReadAsync(calendarEditor.GetAsync(CalendarRequests.Company(2026)));
        await CalendarRequests.SaveCompanyAsync(calendarEditor, 2026, Version(baseline),
            new { date = "2026-12-04", kind = "holiday" });
        var construction = CalendarRequests.Construction(scene.Site.Construction);
        var original = await ReadAsync(scene.Editor.GetAsync(construction));
        await ReadAsync(SendAsync(scene.Editor, HttpMethod.Put, construction, Version(original),
            new { workingWeek = 63, shifts = 2, days = Array.Empty<object>() }));

        var electric = await scene.BuildAsync();
        var civil = await scene.BuildAsync(name: "Общестрой");
        var predecessor = await scene.ManualAsync(civil, "2026-12-01", "2026-12-07");
        var early = await scene.ManualAsync(electric, "2026-12-02", "2026-12-04");
        var onTime = await scene.ManualAsync(electric, "2026-12-03", "2026-12-05");
        var missing = await scene.ManualAsync(electric, end: "2035-06-01");
        var broken = await ReadAsync(scene.LinkAsync(electric, predecessor, early, lag: -2), HttpStatusCode.Created);
        await ReadAsync(scene.LinkAsync(electric, predecessor, onTime, lag: -2), HttpStatusCode.Created);

        var receiving = await ReadAsync(scene.Editor.GetAsync(ScheduleUrl(electric)));
        var source = await ReadAsync(scene.Editor.GetAsync(ScheduleUrl(civil)));
        var first = Assert.Single(Warnings(receiving), w => Code(w) == "starts-before-predecessor");
        var second = Assert.Single(Warnings(source), w => Code(w) == "starts-before-predecessor");
        Assert.Equal(Id(early), GuidOrNull(first, "workId"));
        Assert.Equal(Id(predecessor), GuidOrNull(second, "workId"));
        Assert.Equal(Id(broken), GuidOrNull(first, "linkId"));
        Assert.Equal(Id(broken), GuidOrNull(second, "linkId"));
        Assert.Contains("03.12.2026", first.GetProperty("text").GetString());
        var seenEarly = Assert.Single(Works(receiving), w => Id(w) == Id(early));
        Assert.Contains(Warnings(seenEarly), w => Code(w) == "starts-before-predecessor");
        Assert.Contains(Warnings(seenEarly), w => Code(w) == "ends-on-day-off");
        Assert.Empty(Warnings(Assert.Single(Works(receiving), w => Id(w) == Id(onTime))));

        var gaps = Warnings(Assert.Single(Works(receiving), w => Id(w) == Id(missing)));
        Assert.Contains(gaps, w => Code(w) == "no-dates");
        var gap = Assert.Single(gaps, w => Code(w) == "calendar-missing");
        Assert.Equal(2035, gap.GetProperty("year").GetInt32());
        var again = Assert.Single(Works(await ReadAsync(scene.Editor.GetAsync(ScheduleUrl(electric)))), w => Id(w) == Id(early));
        Assert.Equal("2026-12-02", again.GetProperty("start").GetString());
        Assert.Equal("2026-12-04", again.GetProperty("end").GetString());
        Assert.Equal(Version(early), Version(again));
    }

    private static JsonElement[] Warnings(JsonElement value) => [.. value.GetProperty("warnings").EnumerateArray()];
    private static string? Code(JsonElement warning) => warning.GetProperty("code").GetString();
}
