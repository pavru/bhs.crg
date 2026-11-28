using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Modules;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using BHS.CRG.Modules.Work.Review;
using Microsoft.Extensions.DependencyInjection;
using static BHS.CRG.Tests.Integration.FieldReportClient;
using static BHS.CRG.Tests.Integration.ReviewWorld;

namespace BHS.CRG.Tests.Integration;

[Collection(AssignmentHost.Collection)]
public sealed class FieldReportExchangeTests(AssignmentHost host)
{
    private readonly WorkPeriodWorld _period = new(host);
    private ReportWorld World => _period.World;

    [Fact]
    public async Task Two_devices_replay_an_interrupted_write_and_see_the_office_return_and_deletion()
    {
        await World.SeedAsync();
        var (employee, installer) = await World.InstallerAsync("Монтажник");
        using var web = installer;
        using var office = await ManagerAsync();
        using var phoneA = host.CreateClient();
        using var phoneB = host.CreateClient();
        await ConnectAsync(office, phoneA, employee);
        await ConnectAsync(office, phoneB, employee);
        using var jwtRejected = await office.GetAsync(FieldReportClient.Path);
        Assert.Equal(HttpStatusCode.Unauthorized, jwtRejected.StatusCode);
        var a = new FieldReportClient(phoneA);
        var b = new FieldReportClient(phoneB);
        var id = Guid.NewGuid();
        var line = Guid.NewGuid();
        var input = World.Draft(id, World.SiteA, lineId: line, changeId: Guid.NewGuid());
        using (var lost = await a.CreateAsync(input)) Assert.Equal(HttpStatusCode.Created, lost.StatusCode);
        using var retry = await b.CreateAsync(input);
        var created = await ReadAsync(retry, HttpStatusCode.OK);
        Assert.True(created.GetProperty("replayed").GetBoolean());
        Assert.Equal(line, created.GetProperty("lines")[0].GetProperty("id").GetGuid());

        var change = World.Draft(id, World.SiteA, hours: 9, lineId: line, changeId: Guid.NewGuid());
        await RefusedAsync(a.ChangeAsync(id, null, change), HttpStatusCode.BadRequest, "invalid-request");
        using var written = await a.ChangeAsync(id, Version(created), change);
        var updated = await ReadAsync(written, HttpStatusCode.OK);
        var submitId = Guid.NewGuid();
        using var submit = await b.SubmitAsync(id, Version(updated), submitId, Evening16);
        var submitted = await ReadAsync(submit, HttpStatusCode.OK);
        const string comment = "Уточните объём";
        using var returned = await ReturnAsync(office, id, Version(submitted), comment);
        var latest = await ReadAsync(returned, HttpStatusCode.OK);
        var count = (await World.ChangesAsync(id)).Count;
        using var repeatedChange = await b.ChangeAsync(id, Version(created), change);
        using var repeatedSubmission = await a.SubmitAsync(id, Version(updated), submitId, Evening16);
        foreach (var repeat in new[] { repeatedChange, repeatedSubmission })
        {
            var replay = await ReadAsync(repeat, HttpStatusCode.OK);
            Assert.True(replay.GetProperty("replayed").GetBoolean());
            Assert.Equal(Version(latest), Version(replay));
            Assert.Equal("returned", replay.GetProperty("status").GetString());
            Assert.Equal(comment, replay.GetProperty("returnComment").GetString());
        }
        Assert.Equal(count, (await World.ChangesAsync(id)).Count);
        Assert.Equal(count, (await _period.Review.EntriesAsync(id)).Count);

        var toDelete = Guid.NewGuid();
        using var draft = await web.PostAsJsonAsync(ReportEndpoints.Path, World.Draft(toDelete, World.SiteB));
        var draftBody = await ReadAsync(draft, HttpStatusCode.Created);
        using var removed = await ReportWorld.DeleteAsync(web, $"{ReportEndpoints.Path}/{toDelete}", Version(draftBody));
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        var since = submitted.GetProperty("seq").GetInt64();
        var feed = await a.FeedAsync(since);
        Assert.True(JsonElement.DeepEquals(feed, await b.FeedAsync(since)));
        Assert.Equal(Version(latest), Version(Assert.Single(feed.GetProperty("items").EnumerateArray())));
        Assert.Equal(toDelete, Assert.Single(feed.GetProperty("deleted").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(1, await World.ReportCountAsync());
    }

    [Fact]
    public async Task Manager_collision_preserves_the_web_draft_and_details_until_the_employee_confirms_current_version()
    {
        await World.SeedAsync();
        var employee = await World.EmployeeAsync("Монтажник без учётной записи");
        await World.Assignments.AssignedAsync(employee, World.SiteA, AssignmentKind.Installer, "2026-10-01", "2026-11-15");
        using var office = await ManagerAsync();
        using var phone = host.CreateClient();
        await ConnectAsync(office, phone, employee);
        var field = new FieldReportClient(phone);

        var (id, line, newLine) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.ProjectModuleTypesAsync();
        await ConfigureDetailsAsync();
        object Line(Guid lineId, int volume, object? details = null) =>
            new { id = lineId, volume, workTypeId = World.WorkType, unitId = World.Unit, details };
        object Input(Guid reportId, bool confirm) => new
        {
            id = reportId, constructionId = World.SiteA, date = ReportWorld.Day16, changeId = Guid.NewGuid(),
            confirmManagerEdit = confirm, lines = new[] { Line(line, 130, new { act = "игнорировать" }), Line(newLine, 30) },
        };

        using var webDraft = await office.PostAsJsonAsync(EntryReportEndpoints.Path, new
        {
            id, employeeId = employee, constructionId = World.SiteA, date = ReportWorld.Day16,
            lines = new[] { Line(line, 120, new { act = "АКТ-1", note = "заполнено в вебе" }) },
        });
        var web = await ReadAsync(webDraft, HttpStatusCode.Created);
        using var collision = await field.CreateAsync(Input(Guid.NewGuid(), false));
        var refused = await ReadAsync(collision, HttpStatusCode.Conflict);
        Assert.Equal("manager-report-conflict", refused.GetProperty("reason").GetString());
        Assert.Equal(id, refused.GetProperty("existingId").GetGuid());
        using var opened = await field.GetAsync(id);
        var seen = await ReadAsync(opened, HttpStatusCode.OK);
        Assert.False(seen.GetProperty("assignedOnDate").GetBoolean());
        Assert.True(seen.GetProperty("hasWebFields").GetBoolean());
        Assert.False(seen.GetProperty("lines")[0].TryGetProperty("details", out _));
        await RefusedAsync(field.ChangeAsync(id, Version(seen), Input(id, false)), HttpStatusCode.Conflict,
            "manager-report-conflict");
        await RefusedAsync(field.ChangeAsync(id, "1", Input(id, true)), HttpStatusCode.Conflict, "version-conflict");
        Assert.Single(await _period.Review.EntriesAsync(id));
        Assert.Equal(Version(web), Version(await AsManagerAsync(office, id)));

        using var changed = await field.ChangeAsync(id, Version(seen), Input(id, true));
        var current = await ReadAsync(changed, HttpStatusCode.OK);
        Assert.True(JsonElement.DeepEquals(web.GetProperty("enteredBy"), current.GetProperty("enteredBy")));
        var saved = await AsManagerAsync(office, id);
        Assert.Equal(Version(current), Version(saved));
        var rows = saved.GetProperty("lines");
        Assert.Equal(130m, rows[0].GetProperty("volume").GetDecimal());
        Assert.True(JsonElement.DeepEquals(web.GetProperty("lines")[0].GetProperty("details"),
            rows[0].GetProperty("details")));
        Assert.Empty(rows[1].GetProperty("details").EnumerateObject());
        var entries = await _period.Review.EntriesAsync(id);
        Assert.Equal(2, entries.Count);
        Assert.NotNull(entries[0].AuthorId);
        Assert.Null(entries[1].AuthorId);
        Assert.DoesNotContain("details.", entries[1].Changes);
    }

    [Fact]
    public async Task Closed_period_and_outdated_sender_refuse_writes_while_info_and_refresh_remain_available()
    {
        await World.SeedAsync();
        var employee = await World.EmployeeAsync("Монтажник");
        using var office = await ManagerAsync();
        using var phone = host.CreateClient();
        var credentials = await ConnectAsync(office, phone, employee);
        var field = new FieldReportClient(phone);
        using var closer = await _period.CloserAsync();
        try
        {
            host.ClosureTime = AssignmentHost.Now.AddDays(5);
            await WorkPeriodWorld.CloseAsync(closer);
            var closed = Guid.NewGuid();
            var stale = Guid.NewGuid();
            var info = await phone.GetFromJsonAsync<JsonElement>("/api/field/info");
            var minimum = info.GetProperty("minimumSenderVersionCode").GetInt32();
            await RefusedAsync(field.CreateAsync(World.Draft(closed, World.SiteA, date: WorkPeriodWorld.OctoberEnd,
                changeId: Guid.NewGuid()), minimum), HttpStatusCode.Conflict, "period-closed");
            await RefusedAsync(field.CreateAsync(World.Draft(stale, World.SiteA, changeId: Guid.NewGuid()), minimum - 1),
                HttpStatusCode.Conflict, "client-outdated");
            Assert.Equal(0, await World.ReportCountAsync());
            Assert.Empty(await _period.Review.EntriesAsync(closed));
            Assert.Empty(await _period.Review.EntriesAsync(stale));
            await RotateAsync(phone, credentials);
            Assert.Equal(minimum, (await phone.GetFromJsonAsync<JsonElement>("/api/field/info"))
                .GetProperty("minimumSenderVersionCode").GetInt32());
        }
        finally { host.ClosureTime = null; }
    }

    [Fact]
    public async Task Queue_and_card_render_both_times_and_count_late_delivery_outside_the_current_page()
    {
        await World.SeedAsync();
        var employee = await World.EmployeeAsync("Монтажник");
        using var office = await ManagerAsync();
        using var phone = host.CreateClient();
        await ConnectAsync(office, phone, employee);
        var field = new FieldReportClient(phone);
        var late = Guid.NewGuid();
        var earlier = Guid.NewGuid();
        try
        {
            host.ReportTime = new DateTimeOffset(2026, 11, 16, 14, 0, 0, TimeSpan.Zero);
            using var onTime = await field.CreateAsync(World.Draft(earlier, World.SiteB, changeId: Guid.NewGuid()));
            var prior = await ReadAsync(onTime, HttpStatusCode.Created);
            using var sent = await field.SubmitAsync(earlier, Version(prior), Guid.NewGuid(), host.ReportTime.Value);
            await ReadAsync(sent, HttpStatusCode.OK);

            host.ReportTime = new DateTimeOffset(2026, 11, 16, 23, 0, 0, TimeSpan.Zero);
            using var made = await field.CreateAsync(World.Draft(late, World.SiteA, changeId: Guid.NewGuid(),
                deviceTime: "2026-11-16T20:00:00Z"));
            var draft = await ReadAsync(made, HttpStatusCode.Created);
            using var submitted = await field.SubmitAsync(late, Version(draft), Guid.NewGuid(), Evening16);
            await ReadAsync(submitted, HttpStatusCode.OK);

            var first = await office.GetFromJsonAsync<JsonElement>($"{ReviewQueue.Path}?limit=1");
            Assert.Equal(earlier, first.GetProperty("items")[0].GetProperty("id").GetGuid());
            Assert.Equal(1, first.GetProperty("counts").GetProperty(ReviewMarks.LateDelivery).GetInt32());
            var queue = await office.GetFromJsonAsync<JsonElement>($"{ReviewQueue.Path}?mark={ReviewMarks.LateDelivery}");
            var row = Assert.Single(queue.GetProperty("items").EnumerateArray());
            Assert.Equal(late, row.GetProperty("id").GetGuid());
            var card = await office.GetFromJsonAsync<JsonElement>($"/api/work/review/reports/{late}");
            foreach (var report in new[] { row, card.GetProperty("report") })
            {
                var marks = report.GetProperty("marks").EnumerateArray().ToDictionary(
                    mark => mark.GetProperty("code").GetString()!, mark => mark.GetProperty("text").GetString()!);
                Assert.Contains("120 мин", marks[ReviewMarks.LateDelivery]);
                Assert.Contains("15:00 UTC", marks[ReviewMarks.LateDelivery]);
                Assert.Contains("23:00 UTC", marks[ReviewMarks.LateDelivery]);
                Assert.Contains("480 мин", marks[ReviewMarks.ClockDifference]);
                Assert.Contains("300 мин", marks[ReviewMarks.ClockWentBack]);
            }
        }
        finally { host.ReportTime = null; }
    }

    private async Task<HttpClient> ManagerAsync() =>
        (await World.Assignments.UserAsync("Менеджер", WorkModule.ReportReview, WorkModule.DevicesManage,
            WorkModule.SitesAll)).Client;

    [Fact]
    public async Task Offline_submission_after_auto_submission_records_one_receipt_and_preserves_the_transition()
    {
        await World.SeedAsync();
        var employee = await World.EmployeeAsync("Монтажник");
        using var office = await ManagerAsync();
        using var phone = host.CreateClient();
        await ConnectAsync(office, phone, employee);
        var field = new FieldReportClient(phone);
        var id = Guid.NewGuid();
        using var made = await field.CreateAsync(World.Draft(id, World.SiteA, changeId: Guid.NewGuid()));
        var draft = await ReadAsync(made, HttpStatusCode.Created);
        try
        {
            host.ReportTime = Up19.ToUniversalTime();
            await _period.Review.TickAsync(host.ReportTime.Value);
            using var read = await field.GetAsync(id);
            var auto = await ReadAsync(read, HttpStatusCode.OK);
            Assert.Equal("auto", auto.GetProperty("submittedBy").GetString());
            var change = Guid.NewGuid();
            await RefusedAsync(field.SubmitAsync(id, Version(draft), change, Evening16),
                HttpStatusCode.Conflict, "version-conflict");
            using var arrived = await field.SubmitAsync(id, Version(auto), change, Evening16);
            var delivered = await ReadAsync(arrived, HttpStatusCode.OK);
            Assert.Equal(auto.GetProperty("submittedAt").GetDateTimeOffset(),
                delivered.GetProperty("submittedAt").GetDateTimeOffset());
            using var retry = await field.SubmitAsync(id, Version(auto), change, Evening16);
            var replay = await ReadAsync(retry, HttpStatusCode.OK);
            Assert.True(replay.GetProperty("replayed").GetBoolean());
            Assert.Equal(Version(delivered), Version(replay));
            Assert.Equal([ReportJournalKinds.Created, ReportJournalKinds.AutoSubmitted, ReportJournalKinds.FieldReceipt],
                (await _period.Review.EntriesAsync(id)).Select(entry => entry.Kind));
            var queue = await office.GetFromJsonAsync<JsonElement>(ReviewQueue.Path);
            Assert.Equal(1, queue.GetProperty("counts").GetProperty(ReviewMarks.LateDelivery).GetInt32());
        }
        finally { host.ReportTime = null; }
    }

    private async Task ConfigureDetailsAsync()
    {
        var (_, admin) = await World.Assignments.UserAsync("Администратор типов", CorePermissions.TypesRead, CorePermissions.TypesEdit);
        using (admin)
        {
            var types = await admin.GetFromJsonAsync<JsonElement>("/api/document-types");
            var type = Assert.Single(types.EnumerateArray(),
                item => item.GetProperty("code").GetString() == WorkRecordTypes.LineDetailsCode);
            var id = type.GetProperty("id").GetGuid();
            var record = await admin.GetFromJsonAsync<JsonElement>($"/api/document-types/{id}");
            var schema = JsonNode.Parse(record.GetProperty("schema").GetRawText())!;
            foreach (var (key, title) in new[] { ("act", "Акт"), ("note", "Примечание") })
                schema["fields"]!.AsArray().Add(new JsonObject { ["key"] = key, ["type"] = "string", ["title"] = title });
            using var response = await admin.PutAsJsonAsync($"/api/document-types/{id}/schema", new { schema = schema.ToJsonString() });
            await ReadAsync(response, HttpStatusCode.OK);
        }
    }
}
