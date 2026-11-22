using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using BHS.CRG.Tests.Work;
using Microsoft.Extensions.DependencyInjection;
using static BHS.CRG.Tests.Integration.WorkPeriodWorld;

namespace BHS.CRG.Tests.Integration;

[Collection(AssignmentHost.Collection)]
public class ReportCorrectionTests(AssignmentHost host) : IAsyncLifetime
{
    private readonly WorkPeriodWorld period = new(host);

    public async Task InitializeAsync()
    {
        await period.Review.SeedAsync();
        host.ClosureTime = AssignmentHost.Now.AddDays(5);
    }

    public Task DisposeAsync()
    {
        host.ClosureTime = null;
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Исправление_проверяет_период_версию_причину_и_стройку()
    {
        var site = period.World.SiteA;
        var (_, installer) = await period.World.InstallerAsync("Монтажник");
        var (_, manager) = await period.World.ManagerAsync("Менеджер", site);
        var (_, alien) = await period.World.ManagerAsync("Менеджер другой стройки", period.World.SiteB);
        var (_, all) = await period.World.Assignments.UserAsync("Менеджер всех строек", WorkModule.ReportReview, WorkModule.SitesAll);
        var (id, line) = (Guid.NewGuid(), Guid.NewGuid());
        var accepted = await period.AcceptedAsync(installer, manager, id, site, lineId: line);
        var reopenedView = await installer.GetFromJsonAsync<JsonElement>($"{Reports}/{id}?view=at-closing");
        Assert.Equal("corrected", reopenedView.GetProperty("view").GetString());
        Assert.Empty(reopenedView.GetProperty("versions").EnumerateArray());
        var seen = ReviewWorld.Version(accepted);
        var body = Body(site, line, volume: 99m);
        var open = await PostAsync(manager, id, seen, body);
        Assert.Equal(HttpStatusCode.Conflict, open.StatusCode);
        Assert.Contains("Исправление не нужно", await AssignmentWorld.ErrorAsync(open));

        await CloseAsync(await period.CloserAsync());
        var noVersion = await PostAsync(manager, id, null, body);
        Assert.Equal(HttpStatusCode.BadRequest, noVersion.StatusCode);
        Assert.Contains("If-Match", await AssignmentWorld.ErrorAsync(noVersion));
        var stale = await PostAsync(manager, id, "0", body);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(ReportDesk.Stale, await AssignmentWorld.ErrorAsync(stale));
        var noReason = await PostAsync(manager, id, seen, Body(site, line, 99m, "   "));
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Contains("Укажите причину", await AssignmentWorld.ErrorAsync(noReason));
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(installer, id, seen, body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(alien, id, null, body)).StatusCode);
        Assert.DoesNotContain(await period.Review.EntriesAsync(id), e => e.Kind == ReportJournalKinds.Corrected);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(all, id, seen, body)).StatusCode);
    }

    [Fact]
    public async Task Снимок_берётся_от_закрытия_и_после_отмены_берётся_от_нового()
    {
        var site = period.World.SiteA;
        var (_, installer) = await period.World.InstallerAsync("Монтажник");
        var (_, stranger) = await period.World.InstallerAsync("Другой монтажник");
        var (_, manager) = await period.World.ManagerAsync("Менеджер стройки", site);
        var (_, alien) = await period.World.ManagerAsync("Менеджер другой стройки", period.World.SiteB);
        var closer = await period.CloserAsync();
        var (id, line) = (Guid.NewGuid(), Guid.NewGuid());
        var accepted = await period.AcceptedAsync(installer, manager, id, site, lineId: line);
        await CloseAsync(closer);
        var first = await CorrectAsync(manager, id, ReviewWorld.Version(accepted), Body(site, line, 100m));
        var second = await CorrectAsync(manager, id, ReviewWorld.Version(first), Body(site, line, 90m));
        Assert.Equal(3, second.GetProperty("currentVersion").GetInt32());
        Assert.Equal(90m, Volume(second));
        Assert.Equal("corrected", second.GetProperty("view").GetString());
        var history = second.GetProperty("versions").EnumerateArray().ToArray();
        Assert.Equal([1, 2], history.Select(v => v.GetProperty("number").GetInt32()));
        Assert.Equal("Объём по акту", history[0].GetProperty("reason").GetString());
        Assert.Equal("Менеджер стройки", history[0].GetProperty("author").GetProperty("name").GetString());
        Assert.NotEqual(default, history[0].GetProperty("at").GetDateTimeOffset());
        Assert.Equal(2, (await period.Review.EntriesAsync(id)).Count(e => e.Kind == ReportJournalKinds.Corrected));
        Assert.Equal(2, (await period.Review.CoreRecordsAsync(ReportActions.Corrected.Code, id)).Count);

        var historical = $"{Reports}/{id}?view=at-closing";
        var atClosing = await installer.GetFromJsonAsync<JsonElement>(historical);
        Assert.Equal("at-closing", atClosing.GetProperty("view").GetString());
        Assert.Equal(120m, Volume(atClosing));
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(historical)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alien.GetAsync($"{EntryReportEndpoints.Path}/{id}?view=at-closing")).StatusCode);

        host.ClosureTime = host.ClosureTime!.Value.AddMinutes(1);
        await ReopenCompanyAsync(closer);
        var edited = await ReportWorld.PutAsync(manager, $"{EntryReportEndpoints.Path}/{id}", ReviewWorld.Version(second),
            period.World.Draft(id, site, date: OctoberEnd, lineId: line, volume: 85m, changeId: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        host.ClosureTime = host.ClosureTime!.Value.AddMinutes(1);
        await CloseAsync(closer);
        await CorrectAsync(manager, id, ReviewWorld.Version(await ReportWorld.JsonAsync(edited)), Body(site, line, 80m));
        var closedAgain = await installer.GetFromJsonAsync<JsonElement>(historical);
        Assert.Equal("at-closing", closedAgain.GetProperty("view").GetString());
        Assert.Equal(85m, Volume(closedAgain));
    }

    [Fact]
    public async Task Отмена_закрытия_компании_не_меняет_снимок_стройки_которая_осталась_закрыта()
    {
        var site = period.World.SiteA;
        var (_, installer) = await period.World.InstallerAsync("Монтажник");
        var (_, manager) = await period.World.ManagerAsync("Менеджер стройки", site);
        var closer = await period.CloserAsync();
        var (id, line) = (Guid.NewGuid(), Guid.NewGuid());
        var accepted = await period.AcceptedAsync(installer, manager, id, site, lineId: line);
        var firstClosing = await CloseAsync(closer);
        await CorrectAsync(manager, id, ReviewWorld.Version(accepted), Body(site, line, 100m));
        host.ClosureTime = host.ClosureTime!.Value.AddMinutes(1);
        await CloseAsync(closer, through: "2026-11-01", site: site, from: "2026-11-01", seen: OctoberEnd);
        host.ClosureTime = host.ClosureTime!.Value.AddMinutes(1);
        await ReopenCompanyAsync(closer);

        using var scope = host.Services.CreateScope();
        var periods = scope.ServiceProvider.GetRequiredService<IModulePeriods>();
        Assert.Equal(firstClosing, (await periods.ClosingAsync(site, AssignmentWorld.Day(OctoberEnd)))?.Id);
        var view = await installer.GetFromJsonAsync<JsonElement>($"{Reports}/{id}?view=at-closing");
        Assert.Equal(120m, Volume(view));
    }

    [Fact]
    public async Task Одновременные_исправления_записывают_одну_версию_и_одну_строку_журнала()
    {
        var site = period.World.SiteA;
        var (_, installer) = await period.World.InstallerAsync("Монтажник");
        var (_, manager) = await period.World.ManagerAsync("Менеджер", site);
        var (id, line) = (Guid.NewGuid(), Guid.NewGuid());
        var accepted = await period.AcceptedAsync(installer, manager, id, site, lineId: line);
        await CloseAsync(await period.CloserAsync());

        var seen = ReviewWorld.Version(accepted);
        var answers = await Task.WhenAll(PostAsync(manager, id, seen, Body(site, line, 100m)),
            PostAsync(manager, id, seen, Body(site, line, 105m)));
        var won = Assert.Single(answers, r => r.StatusCode == HttpStatusCode.OK);
        var lost = Assert.Single(answers, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(ReportDesk.Stale, await AssignmentWorld.ErrorAsync(lost));
        Assert.Single((await ReportWorld.JsonAsync(won)).GetProperty("versions").EnumerateArray());
        Assert.Single(await period.Review.EntriesAsync(id), e => e.Kind == ReportJournalKinds.Corrected);
    }

    [Fact]
    public async Task Фото_снятое_при_исправлении_остаётся_доступным_из_снимка_под_изоляцией()
    {
        var site = period.World.SiteA;
        var (_, installer) = await period.World.InstallerAsync("Монтажник");
        var (_, stranger) = await period.World.InstallerAsync("Другой монтажник");
        var (_, manager) = await period.World.ManagerAsync("Менеджер", site);
        var (id, line, photo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var created = await installer.PostAsJsonAsync(Reports, new
        {
            id, constructionId = site, date = OctoberEnd, hours = 8, lines = new[] { new
            {
                id = line, workTypeId = period.World.WorkType, unitId = period.World.Unit, volume = 120,
                photos = new[] { new { id = photo } },
            } },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PhotoWorld.UploadAsync(installer, id, line, photo, PhotoWorld.Shot())).StatusCode);
        var currentUrl = $"/api/work/photos/{photo}";
        var original = await installer.GetByteArrayAsync(currentUrl);
        var submitted = await SubmissionTests.SubmitAsync(installer, $"{Reports}/{id}",
            ReviewWorld.Version(await ReportWorld.JsonAsync(created)), AssignmentHost.Now);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        var accepted = await ReviewWorld.AcceptAsync(manager, id, ReviewWorld.Version(await ReportWorld.JsonAsync(submitted)));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        host.ClosureTime = DateTimeOffset.UtcNow;
        await CloseAsync(await period.CloserAsync());

        await CorrectAsync(manager, id, ReviewWorld.Version(await ReportWorld.JsonAsync(accepted)),
            new { reason = "Строку исключили по акту", constructionId = site, date = OctoberEnd, hours = 8, lines = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.NotFound, (await installer.GetAsync(currentUrl)).StatusCode);
        var history = await installer.GetFromJsonAsync<JsonElement>($"{Reports}/{id}?view=at-closing");
        var url = history.GetProperty("lines")[0].GetProperty("photos")[0].GetProperty("versionUrl").GetString()!;
        Assert.Equal(original, await installer.GetByteArrayAsync(url));
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(url)).StatusCode);
    }

    private object Body(Guid site, Guid line, decimal volume, string reason = "Объём по акту") => new
    {
        reason, constructionId = site, date = OctoberEnd, hours = 7,
        lines = new[] { new { id = line, workTypeId = period.World.WorkType, unitId = period.World.Unit, volume } },
    };

    internal static Task<HttpResponseMessage> PostAsync(HttpClient client, Guid id, string? version, object body) =>
        client.SendAsync(AssignmentWorld.Versioned(HttpMethod.Post, $"{Reports}/{id}/correction", version, body));

    private static async Task<JsonElement> CorrectAsync(HttpClient client, Guid id, string? version, object body)
    {
        var response = await PostAsync(client, id, version, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReportWorld.JsonAsync(response);
    }

    private static decimal Volume(JsonElement report) => report.GetProperty("lines")[0].GetProperty("volume").GetDecimal();
}

[Collection(PlanOnlyCollection.Name)]
public class ReportCorrectionDisabledTests(PlanOnlyHost host)
{
    [Fact]
    public async Task Выключенный_модуль_отвечает_за_воротами()
    {
        using var client = host.CreateClient();
        var response = await ReportCorrectionTests.PostAsync(client, Guid.NewGuid(), "1", new { });
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Equal("work", (await ReportWorld.JsonAsync(response)).GetProperty("module").GetString());
    }
}
