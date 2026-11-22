using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using BHS.CRG.Modules.Work.Review;
using BHS.CRG.Tests.Work;
using static BHS.CRG.Tests.Integration.WorkPeriodWorld;

namespace BHS.CRG.Tests.Integration;

[Collection(AssignmentHost.Collection)]
public class ReportClosedPeriodTests(AssignmentHost host) : IAsyncLifetime
{
    private readonly WorkPeriodWorld period = new(host);

    public async Task InitializeAsync()
    {
        await period.Review.SeedAsync();
        host.ClosureTime = AssignmentHost.Now.AddDays(1);
    }

    public Task DisposeAsync()
    {
        host.ClosureTime = null;
        host.ReportTime = null;
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Пишущие_адреса_сверяют_период_до_версии_под_изоляцией()
    {
        var site = period.World.SiteA;
        var (employee, installer) = await period.World.InstallerAsync("Монтажник");
        var (_, other) = await period.World.InstallerAsync("Другой монтажник");
        var (_, manager) = await period.World.ManagerAsync("Менеджер", site);
        var closer = await period.CloserAsync();
        var (id, line, photo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await period.AcceptedAsync(installer, manager, id, site, lineId: line);

        var url = $"{Reports}/{id}";
        var entry = $"{EntryReportEndpoints.Path}/{id}";
        var photoUrl = $"{url}/lines/{line}/photos/{photo}";
        var requests = new (HttpClient Client, HttpMethod Method, string Url, object? Body)[]
        {
            (installer, HttpMethod.Put, url, new { }),
            (installer, HttpMethod.Delete, url, null),
            (installer, HttpMethod.Post, url + "/submit", null),
            (installer, HttpMethod.Delete, photoUrl, null),
            (manager, HttpMethod.Put, entry, new { }),
            (manager, HttpMethod.Post, entry + "/submit", null),
            (manager, HttpMethod.Post, url + "/accept", null),
            (manager, HttpMethod.Post, url + "/return", null),
        };
        foreach (var (client, method, path, body) in requests)
        {
            var missing = await client.SendAsync(AssignmentWorld.Versioned(method, path, null, body));
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            Assert.Contains("If-Match", await AssignmentWorld.ErrorAsync(missing));
        }

        var before = (await period.World.ChangesAsync(id)).Count;
        await CloseAsync(closer);
        foreach (var (client, method, path, body) in requests)
        {
            var refused = await client.SendAsync(AssignmentWorld.Versioned(method, path, null, body));
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains(url + "/correction", await AssignmentWorld.ErrorAsync(refused));
        }
        var upload = await installer.PutAsync(photoUrl, new ByteArrayContent([]));
        Assert.Equal(HttpStatusCode.Conflict, upload.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync(url, new { })).StatusCode);
        var backdated = await manager.PostAsJsonAsync(EntryReportEndpoints.Path,
            period.World.Draft(Guid.NewGuid(), site, date: OctoberEnd, employee: employee));
        Assert.Equal(HttpStatusCode.Conflict, backdated.StatusCode);
        Assert.Contains("Период закрыт", await AssignmentWorld.ErrorAsync(backdated));
        Assert.Equal(1, await period.World.ReportCountAsync());
        Assert.Equal(before, (await period.World.ChangesAsync(id)).Count);
    }

    [Fact]
    public async Task Нельзя_перенести_открытый_черновик_на_закрытую_дату()
    {
        var site = period.World.SiteA;
        var (_, installer) = await period.World.InstallerAsync("Монтажник");
        var closer = await period.CloserAsync();
        var id = Guid.NewGuid();
        await CloseAsync(closer, site: site);
        var draft = await installer.PostAsJsonAsync(Reports,
            period.World.Draft(id, period.World.SiteB, date: OctoberEnd));
        Assert.Equal(HttpStatusCode.Created, draft.StatusCode);

        var refused = await ReportWorld.PutAsync(installer, $"{Reports}/{id}", null,
            period.World.Draft(id, site, date: OctoberEnd, changeId: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Период закрыт", await AssignmentWorld.ErrorAsync(refused));
        Assert.Equal(period.World.SiteB, (await period.World.ReportAsync(id))!.ConstructionId);
    }

    [Fact]
    public async Task Досылка_возвращает_принятый_отчёт_в_очередь_и_называет_строку_в_журнале()
    {
        var site = period.World.SiteA;
        var (_, installer) = await period.World.InstallerAsync("Монтажник");
        var (_, manager) = await period.World.ManagerAsync("Менеджер", site);
        var (id, original, added) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var accepted = await period.AcceptedAsync(installer, manager, id, site, lineId: original);
        var body = new
        {
            constructionId = site, date = OctoberEnd, hours = 8, changeId = Guid.NewGuid(),
            lines = new[] { Line(original, 120), Line(added, 25) },
        };
        var response = await ReportWorld.PutAsync(installer, $"{Reports}/{id}", ReviewWorld.Version(accepted), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var submitted = await ReportWorld.JsonAsync(response);
        Assert.Equal("submitted", submitted.GetProperty("status").GetString());
        Assert.Contains(submitted.GetProperty("marks").EnumerateArray(), HasMark);

        var queue = await manager.GetFromJsonAsync<JsonElement>(ReviewQueue.Path + "?mark=accepted-changed");
        Assert.Equal(1, queue.GetProperty("counts").GetProperty("accepted-changed").GetInt32());
        Assert.Contains(Assert.Single(queue.GetProperty("items").EnumerateArray()).GetProperty("marks").EnumerateArray(), HasMark);
        var entry = Assert.Single(await period.Review.EntriesAsync(id), e => e.Kind == ReportJournalKinds.AcceptedChanged);
        Assert.True(entry.AfterSubmission);
        using var changes = JsonDocument.Parse(entry.Changes);
        Assert.Contains(changes.RootElement.EnumerateArray(), field =>
            field.GetProperty("line").ValueKind == JsonValueKind.String && field.GetProperty("line").GetGuid() == added);
        var closer = await period.CloserAsync();
        Assert.False((await PreviewAsync(closer)).GetProperty("canClose").GetBoolean());

        object Line(Guid lineId, decimal volume) => new
        {
            id = lineId, workTypeId = period.World.WorkType, unitId = period.World.Unit, volume,
        };
    }

    [Fact]
    public async Task Досланная_правка_с_устройства_до_исходной_подачи_не_помечается_как_поздняя()
    {
        var site = period.World.SiteA;
        var (_, installer) = await period.World.InstallerAsync("Монтажник");
        var (_, manager) = await period.World.ManagerAsync("Менеджер", site);
        var (id, line) = (Guid.NewGuid(), Guid.NewGuid());
        var accepted = await period.AcceptedAsync(installer, manager, id, site, lineId: line);
        var acted = accepted.GetProperty("submittedAt").GetDateTimeOffset().AddMinutes(-1);

        var sent = await ReportWorld.PutAsync(installer, $"{Reports}/{id}", ReviewWorld.Version(accepted),
            period.World.Draft(id, site, date: OctoberEnd, lineId: line, volume: 90m,
                changeId: Guid.NewGuid(), deviceTime: acted.ToString("O")));

        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.Equal("submitted", (await ReportWorld.JsonAsync(sent)).GetProperty("status").GetString());
        var entry = Assert.Single(await period.Review.EntriesAsync(id), e => e.Kind == ReportJournalKinds.AcceptedChanged);
        Assert.False(entry.AfterSubmission);
        Assert.Empty(await period.Review.CoreRecordsAsync(ReportActions.EditedAfterSubmission.Code, id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Доставка_заявленного_фото_сохраняет_состояние_при_равных_или_переведённых_часах(int minutes)
    {
        var closeTime = host.ClosureTime!.Value;
        host.ReportTime = closeTime;
        var site = period.World.SiteA;
        var (_, installer) = await period.World.InstallerAsync("Монтажник");
        var (_, stranger) = await period.World.InstallerAsync("Другой монтажник");
        var (_, manager) = await period.World.ManagerAsync("Менеджер", site);
        var (id, line, beforeClose, afterClose) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        object Content(params Guid[] photos) => new
        {
            constructionId = site, date = OctoberEnd, hours = 8, reason = "Уточнили состав отчёта",
            lines = new[] { new
            {
                id = line, workTypeId = period.World.WorkType, unitId = period.World.Unit, volume = 120,
                photos = photos.Select(photoId => new { id = photoId }).ToArray(),
            } },
        };

        var draft = await installer.PostAsJsonAsync(Reports, new
        {
            id, constructionId = site, date = OctoberEnd,
            lines = new[] { new
            {
                id = line, workTypeId = period.World.WorkType, unitId = period.World.Unit, volume = 120,
                photos = new[] { new { id = beforeClose } },
            } },
        });
        Assert.Equal(HttpStatusCode.Created, draft.StatusCode);
        var url = $"{Reports}/{id}";
        var submitted = await SubmissionTests.SubmitAsync(installer, url,
            ReviewWorld.Version(await ReportWorld.JsonAsync(draft)), AssignmentHost.Now);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        var accepted = await ReviewWorld.AcceptAsync(manager, id, ReviewWorld.Version(await ReportWorld.JsonAsync(submitted)));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var acceptedView = await ReportWorld.JsonAsync(accepted);
        var seen = ReviewWorld.Version(acceptedView);
        var entries = (await period.Review.EntriesAsync(id)).Count;
        var changes = (await period.World.ChangesAsync(id)).Count;
        var closer = await period.CloserAsync();
        await CloseAsync(closer);
        host.ReportTime = closeTime.AddMinutes(minutes);

        var shot = PhotoWorld.Shot();
        Assert.Equal(HttpStatusCode.NotFound,
            (await PhotoWorld.UploadAsync(stranger, id, line, beforeClose, shot)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await PhotoWorld.UploadAsync(installer, id, line, afterClose, shot)).StatusCode);
        var delivered = await PhotoWorld.UploadAsync(installer, id, line, beforeClose, shot);
        Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
        Assert.Equal(host.ReportTime, (await ReportWorld.JsonAsync(delivered)).GetProperty("storedAt").GetDateTimeOffset());
        var replay = await PhotoWorld.UploadAsync(installer, id, line, beforeClose, shot);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(await delivered.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());

        var current = await installer.GetFromJsonAsync<JsonElement>(url);
        Assert.Equal("accepted", current.GetProperty("status").GetString());
        Assert.Equal(seen, ReviewWorld.Version(current));
        Assert.Equal(acceptedView.GetProperty("seq").GetInt64(), current.GetProperty("seq").GetInt64());
        Assert.Empty(current.GetProperty("versions").EnumerateArray());
        Assert.Equal("stored", State(current));
        Assert.Equal(entries, (await period.Review.EntriesAsync(id)).Count);
        Assert.Equal(changes, (await period.World.ChangesAsync(id)).Count);
        var atClosing = await installer.GetFromJsonAsync<JsonElement>(url + "?view=at-closing");
        Assert.Equal("at-closing", atClosing.GetProperty("view").GetString());
        Assert.Equal("pending", State(atClosing));
        Assert.Equal(1, atClosing.GetProperty("photosPending").GetInt32());

        var corrected = await ReportCorrectionTests.PostAsync(manager, id, seen, Content(afterClose));
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await PhotoWorld.UploadAsync(installer, id, line, afterClose, shot)).StatusCode);
        Assert.Equal(HttpStatusCode.Gone,
            (await PhotoWorld.UploadAsync(installer, id, line, beforeClose, shot)).StatusCode);
        var snapshot = await installer.GetFromJsonAsync<JsonElement>(url + "?view=at-closing");
        Assert.Equal("pending", State(snapshot));
        Assert.False(snapshot.GetProperty("lines")[0].GetProperty("photos")[0].TryGetProperty("versionUrl", out _));

        host.ClosureTime = closeTime.AddMinutes(1);
        await ReopenCompanyAsync(closer);
        host.ClosureTime = closeTime.AddMinutes(2);
        await CloseAsync(closer);
        Assert.Equal(HttpStatusCode.OK, (await PhotoWorld.UploadAsync(installer, id, line, afterClose, shot)).StatusCode);
        Assert.Equal("stored", State(await installer.GetFromJsonAsync<JsonElement>(url)));
        Assert.Equal("pending", State(await installer.GetFromJsonAsync<JsonElement>(url + "?view=at-closing")));

        static string? State(JsonElement report) =>
            Assert.Single(report.GetProperty("lines")[0].GetProperty("photos").EnumerateArray()).GetProperty("state").GetString();
    }

    private static bool HasMark(JsonElement mark) =>
        mark.GetProperty("code").GetString() == "accepted-changed" && mark.GetProperty("text").GetString() == "изменение принятого";
}
