using System.Net;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static BHS.CRG.Tests.Plan.OfferDerivativeScenario;
using static BHS.CRG.Tests.Plan.ScheduleScenario;

namespace BHS.CRG.Tests.Plan;

[Collection(PlanOnlyCollection.Name)]
public class ScheduleCycleCheckTests(PlanOnlyHost host)
{
    [Fact]
    public async Task Цикл_через_два_графика_называет_работы_и_ничего_не_пишет()
    {
        var scene = await CreateAsync(host);
        var electric = await scene.BuildAsync();
        var civil = await scene.BuildAsync(name: "Общестрой");
        var a = await scene.ManualAsync(electric, sectionId: scene.Site.Section);
        var otherSection = await DistributionFlow.SectionAsync(host, scene.Site.Construction, "ЭОМ-2");
        var b = await scene.ManualAsync(civil, sectionId: otherSection);
        var c = await scene.ManualAsync(electric);
        var ab = await ReadAsync(scene.LinkAsync(civil, a, b, lag: -2), HttpStatusCode.Created);
        await ReadAsync(scene.LinkAsync(electric, b, c), HttpStatusCode.Created);
        Assert.Equal(-2, ab.GetProperty("lag").GetInt32());

        var error = await ReadAsync(scene.LinkAsync(electric, c, a), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("schedule-cycle", error.GetProperty("code").GetString());
        var cycle = error.GetProperty("cycle").EnumerateArray().ToList();
        Assert.Equal([Id(c), Id(a), Id(b), Id(c)], cycle.Select(Id).ToArray());
        Assert.Equal("Общестрой", cycle[2].GetProperty("scheduleName").GetString());
        Assert.Equal([null, scene.Site.Section, otherSection, null], cycle.Select(item => GuidOrNull(item, "sectionId")));
        var publicView = await ReadAsync(scene.Editor.GetAsync(ScheduleUrl(civil)));
        var links = publicView.GetProperty("links").EnumerateArray().ToList();
        Assert.Equal(2, links.Count);
        var saved = Assert.Single(links, link => Id(link) == Id(ab));
        Assert.Equal(Version(ab), Version(saved));
        Assert.Equal(scene.Site.Section, GuidOrNull(saved.GetProperty("predecessor"), "sectionId"));
        Assert.Equal(otherSection, GuidOrNull(saved.GetProperty("successor"), "sectionId"));

        var foreign = await CreateAsync(host);
        var otherSchedule = await foreign.BuildAsync();
        var otherWork = await foreign.ManualAsync(otherSchedule);
        var crossSite = await ReadAsync(scene.LinkAsync(civil, otherWork, b), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("other-construction", crossSite.GetProperty("code").GetString());
        var unsupported = await ReadAsync(scene.LinkAsync(civil, a, b, kind: "ss"), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("unsupported-kind", unsupported.GetProperty("code").GetString());

        using var removed = await scene.Editor.SendAsync(RateMatchFlow.With(HttpMethod.Delete,
            WorkUrl(civil, b), Version(b)));
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Empty((await ReadAsync(scene.Editor.GetAsync(ScheduleUrl(electric)))).GetProperty("links").EnumerateArray());
    }

    [Fact]
    public async Task Две_вставки_позади_одного_замка_не_могут_вместе_замкнуть_цикл()
    {
        var scene = await CreateAsync(host);
        var electric = await scene.BuildAsync();
        var civil = await scene.BuildAsync(name: "Общестрой");
        var a = await scene.ManualAsync(electric);
        var b = await scene.ManualAsync(civil);
        var c = await scene.ManualAsync(electric);
        await ReadAsync(scene.LinkAsync(civil, a, b), HttpStatusCode.Created);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var key = $"plan:schedules:{scene.Site.Construction}";
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))");
        var forward = scene.LinkAsync(electric, b, c);
        var reverse = scene.LinkAsync(electric, c, a);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        // Оба HTTP-запроса действительно дошли до транзакционного замка до освобождения очереди.
        while (await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM pg_locks waiting
            JOIN pg_locks held USING (locktype, database, classid, objid, objsubid)
            WHERE held.pid = pg_backend_pid() AND held.granted AND held.locktype = 'advisory' AND NOT waiting.granted
            """).SingleAsync(timeout.Token) < 2)
            await Task.Delay(30, timeout.Token);
        await transaction.CommitAsync();

        var results = await Task.WhenAll(forward, reverse);
        Assert.Single(results, result => result.StatusCode == HttpStatusCode.Created);
        var refused = Assert.Single(results, result => result.StatusCode == HttpStatusCode.UnprocessableEntity);
        var error = await ReadAsync(Task.FromResult(refused), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("schedule-cycle", error.GetProperty("code").GetString());
        Assert.Equal(2, (await ReadAsync(scene.Editor.GetAsync(ScheduleUrl(electric)))).GetProperty("links").GetArrayLength());
    }

    [Fact]
    public async Task Правка_и_удаление_связи_требуют_версию_и_право_правки()
    {
        var scene = await CreateAsync(host);
        var schedule = await scene.BuildAsync();
        var a = await scene.ManualAsync(schedule);
        var b = await scene.ManualAsync(schedule);
        var link = await ReadAsync(scene.LinkAsync(schedule, a, b), HttpStatusCode.Created);
        var path = $"{ScheduleUrl(schedule)}/links/{Id(link)}";
        var body = Link(a, b, lag: 1);
        var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.ScheduleRead);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(reader, HttpMethod.Put, path, Version(link), body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(scene.Editor, HttpMethod.Put, path, null, body)).StatusCode);
        var updated = await ReadAsync(SendAsync(scene.Editor, HttpMethod.Put, path, Version(link), body));
        Assert.Equal(1, updated.GetProperty("lag").GetInt32());
        Assert.NotEqual(Version(link), Version(updated));
        using var stale = await scene.Editor.SendAsync(RateMatchFlow.With(HttpMethod.Delete, path, Version(link)));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var deleted = await scene.Editor.SendAsync(RateMatchFlow.With(HttpMethod.Delete, path, Version(updated)));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty((await ReadAsync(scene.Editor.GetAsync(ScheduleUrl(schedule)))).GetProperty("links").EnumerateArray());
    }
}
