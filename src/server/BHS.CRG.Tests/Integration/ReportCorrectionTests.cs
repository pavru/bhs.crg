using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using BHS.CRG.Tests.Work;
using Microsoft.EntityFrameworkCore;
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
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(manager, id, seen, Body(site, line, 99m, new string('а', 1001)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(manager, id, "not-a-version", body)).StatusCode);
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
        var entries = (await period.Review.EntriesAsync(id)).Where(e => e.Kind == ReportJournalKinds.Corrected).ToArray();
        Assert.Equal(2, entries.Length);
        Assert.All(entries, e => Assert.Equal("Объём по акту", e.Comment));
        var audits = await period.Review.CoreRecordsAsync(ReportActions.Corrected.Code, id);
        Assert.Equal(2, audits.Count);
        Assert.All(audits, a =>
        {
            Assert.Equal("исправлен", a.After);
            Assert.Equal("Менеджер стройки", a.ActorName);
            Assert.Equal(id.ToString(), a.TargetId);
        });

        var historical = $"{Reports}/{id}?view=at-closing";
        var atClosing = await installer.GetFromJsonAsync<JsonElement>(historical);
        Assert.Equal("at-closing", atClosing.GetProperty("view").GetString());
        Assert.Equal(120m, Volume(atClosing));
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(historical)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alien.GetAsync($"{EntryReportEndpoints.Path}/{id}?view=at-closing")).StatusCode);

        var cardUrl = $"/api/work/review/reports/{id}";
        foreach (var (number, volume, view) in new[] { (1, 120m, "at-closing"), (2, 100m, "archived"), (3, 90m, "corrected") })
        {
            var card = await manager.GetFromJsonAsync<JsonElement>($"{cardUrl}?number={number}");
            Assert.Equal(number, card.GetProperty("viewedVersion").GetInt32());
            Assert.Equal(1, card.GetProperty("atClosingVersion").GetInt32());
            Assert.Equal(number == 3 ? ["correct"] : Array.Empty<string>(),
                card.GetProperty("actions").EnumerateArray().Select(x => x.GetString()));
            var version = card.GetProperty("report");
            Assert.Equal(volume, Volume(version));
            Assert.Equal(view, version.GetProperty("view").GetString());
        }
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"{cardUrl}?number=4")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alien.GetAsync($"{cardUrl}?number=2")).StatusCode);

        host.ClosureTime = host.ClosureTime!.Value.AddMinutes(1);
        await ReopenCompanyAsync(closer);
        Assert.Empty((await manager.GetFromJsonAsync<JsonElement>(cardUrl)).GetProperty("actions").EnumerateArray());
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

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Перекрытие_закрытий_при_сдвиге_часов_сохраняет_снимок_и_запрет_догрузки(int minutes)
    {
        var site = period.World.SiteA;
        var (_, installer) = await period.World.InstallerAsync("Монтажник");
        var (_, manager) = await period.World.ManagerAsync("Менеджер стройки", site);
        var closer = await period.CloserAsync();
        var (id, line, photo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var accepted = await period.AcceptedAsync(installer, manager, id, site, lineId: line);
        var firstAt = host.ClosureTime!.Value;
        var firstClosing = await CloseAsync(closer);
        await CorrectAsync(manager, id, ReviewWorld.Version(accepted), Body(site, line, 100m, photo: photo));
        host.ClosureTime = firstAt.AddMinutes(minutes);
        await CloseAsync(closer, through: "2026-11-01", site: site, from: "2026-11-01", seen: OctoberEnd);

        var historicalUrl = $"{Reports}/{id}?view=at-closing";
        var view = await installer.GetFromJsonAsync<JsonElement>(historicalUrl);
        Assert.Equal(ReportHistory.AtClosing, view.GetProperty("view").GetString());
        Assert.Equal(120m, Volume(view));
        Assert.Equal(HttpStatusCode.Conflict,
            (await PhotoWorld.UploadAsync(installer, id, line, photo, PhotoWorld.Shot())).StatusCode);

        host.ClosureTime = firstAt.AddMinutes(-2);
        await ReopenCompanyAsync(closer);

        using var scope = host.Services.CreateScope();
        var periods = scope.ServiceProvider.GetRequiredService<IModulePeriods>();
        Assert.Equal(firstClosing, (await periods.ClosingAsync(site, AssignmentWorld.Day(OctoberEnd)))?.Id);
        Assert.Equal(120m, Volume(await installer.GetFromJsonAsync<JsonElement>(historicalUrl)));
    }

    [Fact]
    public async Task Чтение_на_момент_закрытия_удерживает_отчёт_и_границу_до_конца()
    {
        var site = period.World.SiteA;
        var (employee, installer) = await period.World.InstallerAsync("Монтажник");
        var (_, manager) = await period.World.ManagerAsync("Менеджер", site);
        var closer = await period.CloserAsync();
        var (id, line) = (Guid.NewGuid(), Guid.NewGuid());
        var accepted = await period.AcceptedAsync(installer, manager, id, site, lineId: line);
        await CloseAsync(closer);
        var first = await CorrectAsync(manager, id, ReviewWorld.Version(accepted), Body(site, line, 100m));

        using var readerScope = host.Services.CreateScope();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var gate = new PausedClosingPort(readerScope.ServiceProvider.GetRequiredService<IModulePeriods>());
        var reader = ActivatorUtilities.CreateInstance<ReportHistory>(readerScope.ServiceProvider, (IModulePeriods)gate);
        var reading = reader.GetAsync(ReportViewer.Own(employee), id, ReportHistory.AtClosing, timeout.Token);
        try
        {
            await gate.Entered.Task.WaitAsync(timeout.Token);
            using var probeScope = host.Services.CreateScope();
            var db = probeScope.ServiceProvider.GetRequiredService<WorkDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync(timeout.Token);
            var canReopen = await db.Database
                .SqlQuery<bool>($"""SELECT pg_try_advisory_xact_lock({OpenPeriodWrite.LockKey}) AS "Value" """)
                .SingleAsync(timeout.Token);
            var canCorrect = await db.Database
                .SqlQuery<bool>($"""SELECT pg_try_advisory_xact_lock(hashtextextended({WorkDbContext.ReportLockKey(employee)}, 0)) AS "Value" """)
                .SingleAsync(timeout.Token);
            Assert.False(canReopen);
            Assert.False(canCorrect);
        }
        finally
        {
            gate.Resume.TrySetResult();
            await reading;
        }
        var historical = await reading;
        Assert.Equal(ReportHistory.AtClosing, historical.View);
        Assert.Equal(120m, Assert.Single(historical.Lines).Volume);

        await CorrectAsync(manager, id, ReviewWorld.Version(first), Body(site, line, 90m));
        host.ClosureTime = host.ClosureTime!.Value.AddMinutes(1);
        await ReopenCompanyAsync(closer);
        var open = await installer.GetFromJsonAsync<JsonElement>($"{Reports}/{id}?view=at-closing");
        Assert.Equal("corrected", open.GetProperty("view").GetString());
        Assert.Equal(90m, Volume(open));
    }

    private sealed class PausedClosingPort(IModulePeriods inner) : IModulePeriods
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PeriodBoundaries> BoundariesAsync(CancellationToken ct = default) => inner.BoundariesAsync(ct);

        public async Task<(Guid Id, DateTimeOffset At)?> ClosingAsync(Guid constructionId, DateOnly date,
            CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await Resume.Task.WaitAsync(ct);
            return await inner.ClosingAsync(constructionId, date, ct);
        }
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
        var retained = Guid.NewGuid();
        var created = await installer.PostAsJsonAsync(Reports, new
        {
            id, constructionId = site, date = OctoberEnd, shift = "night", hours = 8, lines = new object[] { new
            {
                id = line, workTypeId = period.World.WorkType, unitId = period.World.Unit, volume = 120,
                photos = new[] { new { id = photo } },
            }, new
            {
                id = retained, workTypeId = period.World.WorkType, unitId = period.World.Unit, volume = 120,
                photos = Array.Empty<object>(),
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

        var card = await manager.GetFromJsonAsync<JsonElement>($"/api/work/review/reports/{id}");
        var version = ReviewWorld.Version(card.GetProperty("report"));
        var added = Guid.NewGuid();
        var body = new
        {
            reason = "Строку исключили по акту", constructionId = site, date = OctoberEnd, shift = "night", hours = 6,
            note = "По табелю", lines = new[]
            {
                new { id = added, workTypeId = period.World.WorkType, unitId = period.World.Unit, volume = 10 },
                new { id = retained, workTypeId = period.World.WorkType, unitId = period.World.Unit, volume = 90 },
            },
        };
        var corrected = await CorrectAsync(manager, id, version, body);
        Assert.Equal("night", corrected.GetProperty("shift").GetString());
        Assert.Equal(6m, corrected.GetProperty("hours").GetDecimal());
        Assert.Equal("По табелю", corrected.GetProperty("note").GetString());
        Assert.Equal([added, retained], corrected.GetProperty("lines").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()));
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(manager, id, version, body)).StatusCode);
        Assert.Single(await period.Review.EntriesAsync(id), e => e.Kind == ReportJournalKinds.Corrected);
        Assert.Equal(HttpStatusCode.NotFound, (await installer.GetAsync(currentUrl)).StatusCode);
        var history = await installer.GetFromJsonAsync<JsonElement>($"{Reports}/{id}?view=at-closing");
        Assert.Equal(8m, history.GetProperty("hours").GetDecimal());
        var url = history.GetProperty("lines")[0].GetProperty("photos")[0].GetProperty("versionUrl").GetString()!;
        Assert.Equal(original, await installer.GetByteArrayAsync(url));
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(url)).StatusCode);
    }

    private object Body(Guid site, Guid line, decimal volume, string reason = "Объём по акту", Guid? photo = null) => new
    {
        reason, constructionId = site, date = OctoberEnd, hours = 7,
        lines = new[] { new
        {
            id = line, workTypeId = period.World.WorkType, unitId = period.World.Unit, volume,
            photos = photo is { } id ? new[] { new { id } } : null,
        } },
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
