using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using BHS.CRG.Modules.Work.Review;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

[Collection(AssignmentHost.Collection)]
public class WorkReportAcceptanceFieldsTests(AssignmentHost host) : IAsyncLifetime
{
    private const string Types = "/api/document-types";
    private const string Reports = ReportEndpoints.Path;
    private readonly WorkPeriodWorld period = new(host);
    private ReportWorld World => period.World;

    public async Task InitializeAsync()
    {
        await period.Review.SeedAsync();
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.ProjectModuleTypesAsync();
    }

    public Task DisposeAsync()
    {
        host.ClosureTime = null;
        host.ReportTime = null;
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Схема_помечает_только_сведения_и_доступна_роли_ввода()
    {
        var (admin, type) = await ConfigureAsync();
        var employeeType = await admin.GetFromJsonAsync<JsonElement>($"{Types}/{World.EmployeeType}");
        Assert.False(employeeType.GetProperty("acceptanceFields").GetBoolean());
        var (_, installer) = await World.InstallerAsync("Монтажник");
        Assert.Equal(HttpStatusCode.Forbidden, (await installer.GetAsync(Types)).StatusCode);
        var form = await installer.GetFromJsonAsync<JsonElement>(ReportDetailsSchema.Path);
        Assert.Equal(["act", "test", "note"], form.GetProperty("fields").EnumerateArray()
            .Select(field => field.GetProperty("key").GetString()));
        var (_, denied) = await World.Assignments.UserAsync("Наблюдатель", WorkModule.ReportRead);
        Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync(ReportDetailsSchema.Path)).StatusCode);

        var original = (await admin.GetFromJsonAsync<JsonElement>($"{Types}/{type}")).GetProperty("schema");
        var payload = JsonNode.Parse(original.GetRawText())!;
        payload["fields"]![0]!["forAcceptance"] = true;
        var refused = await admin.PutAsJsonAsync($"{Types}/{type}/schema", new { schema = payload.ToJsonString() });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("поля модуля", await AssignmentWorld.ErrorAsync(refused));

        using var open = await admin.PostAsJsonAsync(Types, new { name = "Обычный тип", code = "Ordinary",
            kind = "Composite", module = "core", schema = """{"fields":[{"key":"act","title":"Акт","forAcceptance":true}]}""" });
        Assert.Equal(HttpStatusCode.Conflict, open.StatusCode);
        Assert.Contains("нет приёмки", await AssignmentWorld.ErrorAsync(open));
    }

    [Fact]
    public async Task Ссылки_выбираются_из_семьи_поля_в_охвате_отчёта_без_общих_прав()
    {
        var (admin, type) = await ConfigureAsync();
        var seed = new WorkTypeSeed(host);
        var target = await seed.TypeAsync("СсылочнаяЦель", "Цель ссылки");
        var child = await seed.TypeAsync("СсылочнаяПодцель", "Подтип цели", parentId: target.Id);
        await AddFieldsAsync(admin, type,
            new JsonObject { ["key"] = "target", ["type"] = "doc-ref", ["typeId"] = target.Id, ["forAcceptance"] = true },
            new JsonObject { ["key"] = "targets", ["type"] = "doc-array", ["typeId"] = target.Id, ["forAcceptance"] = true });
        var siteRecord = await seed.SendAsync(new CreateCommonDataEntryCommand("На стройке", target.Id,
            JsonDocument.Parse("{}"), CatalogScope.Construction, World.SiteA));
        var unrelatedSite = await seed.SendAsync(new CreateCommonDataEntryCommand("Чужая стройка", child.Id,
            JsonDocument.Parse("{}"), CatalogScope.Construction, World.SiteB));
        var systemRecord = await seed.RecordAsync(child.Id, "Из подтипа");
        var archive = await seed.RecordAsync(child.Id, "В архиве");
        await seed.ArchiveAsync(archive.Id);
        var (_, installer) = await World.InstallerAsync("Монтажник");
        var (_, manager) = await World.ManagerAsync("Менеджер", World.SiteA);
        var (id, line) = (Guid.NewGuid(), Guid.NewGuid());
        var submitted = await period.Review.SubmittedAsync(installer, id, World.SiteA, lineId: line);
        var url = $"/api/work/entry/sites/{World.SiteA}/catalog/field-references?reportId={id}&typeId={target.Id}";
        Assert.Equal(HttpStatusCode.Forbidden, (await installer.GetAsync("/api/common-data/for-scope?scope=System&purpose=choice")).StatusCode);

        foreach (var client in new[] { installer, manager })
        {
            var page = await client.GetFromJsonAsync<JsonElement>(url);
            Assert.Equal(new[] { siteRecord.Id, systemRecord.Id }, ChoiceIds(page));
            Assert.DoesNotContain(unrelatedSite.Id, ChoiceIds(page));
            Assert.All(page.GetProperty("items").EnumerateArray(), item => Assert.False(item.TryGetProperty("data", out _)));
            Assert.Equal(new[] { systemRecord.Id }, ChoiceIds(await client.GetFromJsonAsync<JsonElement>(url + "&q=подтипа")));
            Assert.Equal(new[] { archive.Id }, ChoiceIds(await client.GetFromJsonAsync<JsonElement>(url + "&archived=true")));
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url.Replace(World.SiteA.ToString(), World.SiteB.ToString()))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url.Replace(target.Id.ToString(), World.EmployeeType.ToString()))).StatusCode);
        }

        using var saved = await ReportWorld.PutAsync(installer, $"{Reports}/{id}", ReviewWorld.Version(submitted),
            WithDetails(id, World.SiteA, line, new { target = WorkTypeSeed.Ref(siteRecord), targets = new[] { WorkTypeSeed.Ref(systemRecord) } }));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var report = await ReportWorld.JsonAsync(saved);
        Assert.DoesNotContain("target", Keys(report));
        Assert.DoesNotContain("targets", Keys(report));
    }

    [Fact]
    public async Task Вложение_сведений_сохраняет_владельца_ядра_и_читается_после_восстановления()
    {
        var (admin, type) = await ConfigureAsync();
        await AddFieldsAsync(admin, type, new JsonObject { ["key"] = "attachment", ["type"] = "file", ["forAcceptance"] = true });
        var (_, installer) = await World.InstallerAsync("Монтажник");
        using var scope = host.Services.CreateScope();
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobStorage>();
        var backup = scope.ServiceProvider.GetRequiredService<BackupService>();
        var bytes = Encoding.UTF8.GetBytes("Документ в сведениях строки");
        using var file = new MemoryStream(bytes);
        var path = await blobs.UploadAsync("act.txt", file, "text/plain");
        var id = Guid.NewGuid();
        using var created = await installer.PostAsJsonAsync(Reports, WithDetails(id, World.SiteA, Guid.NewGuid(),
            new { attachment = new Dictionary<string, object>
                { ["$type"] = "file", ["blobPath"] = path, ["fileName"] = "act.txt", ["mimeType"] = "text/plain", ["size"] = bytes.Length } }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var (stream, _) = await backup.ExportAsync(BackupScope.Full);
        await using var exported = stream;
        using (var zip = new ZipArchive(exported, ZipArchiveMode.Read, leaveOpen: true))
        using (var manifestEntry = zip.GetEntry("manifest.json")!.Open())
        {
            var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(manifestEntry);
            Assert.Contains(path, Assert.Single(manifest!.ModuleData!, section => section.Module == WorkModule.ModuleCode).Files!);
            Assert.Equal("core", manifest.FileOwnerOverrides![path]);
            Assert.NotNull(zip.GetEntry($"blobs/{path}"));
        }
        exported.Position = 0;
        await blobs.DeleteAsync(path);
        var restored = await backup.ImportAsync(exported);
        Assert.True(restored.Success, string.Join("; ", restored.Warnings));
        await using var downloaded = await blobs.DownloadAsync(path);
        using var actual = new MemoryStream();
        await downloaded.CopyToAsync(actual);
        Assert.Equal(bytes, actual.ToArray());
    }

    [Fact]
    public async Task Очередь_считает_по_охвату_и_снятие_признака_действует_сразу()
    {
        var (admin, type) = await ConfigureAsync();
        var (_, first) = await World.InstallerAsync("Монтажник А");
        var (_, full) = await World.InstallerAsync("Монтажник Б");
        var (_, alien) = await World.InstallerAsync("Монтажник В");
        var (_, manager) = await World.ManagerAsync("Менеджер", World.SiteA);
        var (id, line, filled, alienId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var submitted = await period.Review.SubmittedAsync(first, id, World.SiteA, lineId: line);
        Assert.Equal(2, Missing(submitted));
        Assert.Equal(["act", "test"], Keys(submitted));
        await period.Review.SubmittedAsync(alien, alienId, World.SiteB);
        await SubmitFilledAsync(full, filled, World.SiteA);
        Assert.Equal(HttpStatusCode.NotFound, (await alien.GetAsync($"{Reports}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"{EntryReportEndpoints.Path}/{alienId}")).StatusCode);

        var all = await manager.GetFromJsonAsync<JsonElement>(ReviewQueue.Path);
        Assert.Equal(2, all.GetProperty("total").GetInt32());
        var filledRow = Assert.Single(all.GetProperty("items").EnumerateArray(), row => row.GetProperty("id").GetGuid() == filled);
        Assert.DoesNotContain(filledRow.GetProperty("marks").EnumerateArray(), m => Code(m) == ReviewMarks.FieldsMissing);
        var filtered = await manager.GetFromJsonAsync<JsonElement>($"{ReviewQueue.Path}?mark={ReviewMarks.FieldsMissing}&limit=1");
        Assert.Equal(1, filtered.GetProperty("counts").GetProperty(ReviewMarks.FieldsMissing).GetInt32());
        Assert.Equal(1, filtered.GetProperty("total").GetInt32());
        var row = Assert.Single(filtered.GetProperty("items").EnumerateArray());
        Assert.Equal(id, row.GetProperty("id").GetGuid());
        var mark = Assert.Single(row.GetProperty("marks").EnumerateArray(), m => Code(m) == ReviewMarks.FieldsMissing);
        Assert.Equal("не заполнены поля: 2", mark.GetProperty("text").GetString());

        var accepted = await ReviewWorld.AcceptAsync(manager, id, ReviewWorld.Version(submitted));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var card = await manager.GetFromJsonAsync<JsonElement>($"/api/work/review/reports/{id}");
        Assert.Equal("accepted", card.GetProperty("status").GetString());
        Assert.Equal(2, Missing(card.GetProperty("report")));

        await SetFlagsAsync(admin, type, marked: false);
        Assert.Equal(0, Missing(await first.GetFromJsonAsync<JsonElement>($"{Reports}/{id}")));
        var fresh = await manager.GetFromJsonAsync<JsonElement>(ReviewQueue.Path);
        Assert.Equal(0, fresh.GetProperty("counts").GetProperty(ReviewMarks.FieldsMissing).GetInt32());
    }

    [Fact]
    public async Task После_подачи_изменение_по_версии_с_журналом()
    {
        await ConfigureAsync();
        var (_, installer) = await World.InstallerAsync("Монтажник");
        var (_, manager) = await World.ManagerAsync("Менеджер", World.SiteA);
        var (_, denied) = await World.Assignments.UserAsync("Наблюдатель", WorkModule.ReportRead);
        var (id, line) = (Guid.NewGuid(), Guid.NewGuid());
        var submitted = await period.Review.SubmittedAsync(installer, id, World.SiteA, lineId: line);
        var body = WithDetails(id, World.SiteA, line, new { act = "А-17" });
        var route = $"{Reports}/{id}";
        Assert.Equal(HttpStatusCode.BadRequest, (await ReportWorld.PutAsync(installer, route, null, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ReportWorld.PutAsync(installer, route, "0", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ReportWorld.PutAsync(denied, route, null, body)).StatusCode);
        Assert.Equal(2, Missing(await installer.GetFromJsonAsync<JsonElement>(route)));

        host.ReportTime = ReviewWorld.Evening16.AddHours(1).ToUniversalTime();
        var changed = await ReportWorld.PutAsync(installer, route, ReviewWorld.Version(submitted), body);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(["test"], Keys(await ReportWorld.JsonAsync(changed)));
        var entry = Assert.Single(await period.Review.EntriesAsync(id), e => e.Kind == ReportJournalKinds.Updated);
        Assert.True(entry.AfterSubmission);
        using var changes = JsonDocument.Parse(entry.Changes);
        var field = Assert.Single(changes.RootElement.EnumerateArray(), item => item.GetProperty("field").GetString() == "details.act");
        Assert.Equal(JsonValueKind.Null, field.GetProperty("before").ValueKind);
        Assert.Equal("А-17", field.GetProperty("after").GetString());
        var queue = await manager.GetFromJsonAsync<JsonElement>(ReviewQueue.Path);
        var row = Assert.Single(queue.GetProperty("items").EnumerateArray());
        Assert.Contains(row.GetProperty("marks").EnumerateArray(), m => Code(m) == ReviewMarks.EditedAfterSubmit);
    }

    [Fact]
    public async Task Закрытый_отчёт_заполняется_новой_версией_и_снимок_сохраняет_пустоту()
    {
        await ConfigureAsync();
        host.ClosureTime = AssignmentHost.Now.AddDays(5);
        var site = World.SiteA;
        var (_, installer) = await World.InstallerAsync("Монтажник");
        var (_, manager) = await World.ManagerAsync("Менеджер", site);
        var (id, line) = (Guid.NewGuid(), Guid.NewGuid());
        var accepted = await period.AcceptedAsync(installer, manager, id, site, lineId: line);
        var version = ReviewWorld.Version(accepted);
        await WorkPeriodWorld.CloseAsync(await period.CloserAsync());
        var body = WithDetails(id, site, line, new { act = "А-17", test = "П-28" }, WorkPeriodWorld.OctoberEnd,
            "Дополнено по протоколу");
        var direct = await ReportWorld.PutAsync(manager, $"{EntryReportEndpoints.Path}/{id}", version, body);
        Assert.Equal(HttpStatusCode.Conflict, direct.StatusCode);
        Assert.Contains("новой версией", await AssignmentWorld.ErrorAsync(direct));
        Assert.Equal(2, Missing(await installer.GetFromJsonAsync<JsonElement>($"{Reports}/{id}")));

        var fixedReport = await manager.SendAsync(AssignmentWorld.Versioned(HttpMethod.Post, $"{Reports}/{id}/correction", version, body));
        Assert.Equal(HttpStatusCode.OK, fixedReport.StatusCode);
        var corrected = await ReportWorld.JsonAsync(fixedReport);
        Assert.Equal(2, corrected.GetProperty("currentVersion").GetInt32());
        Assert.Equal(0, Missing(corrected));
        var atClosing = await installer.GetFromJsonAsync<JsonElement>($"{Reports}/{id}?view=at-closing");
        Assert.Equal(2, Missing(atClosing));
        Assert.Equal(["act", "test"], Keys(atClosing));
    }

    private async Task<(HttpClient Admin, Guid Type)> ConfigureAsync()
    {
        var (_, admin) = await World.Assignments.UserAsync("Администратор типов", CorePermissions.TypesRead, CorePermissions.TypesEdit);
        var types = await admin.GetFromJsonAsync<JsonElement>(Types);
        var type = Assert.Single(types.EnumerateArray(), item => item.GetProperty("code").GetString() == WorkRecordTypes.LineDetailsCode);
        var id = type.GetProperty("id").GetGuid();
        Assert.True(type.GetProperty("acceptanceFields").GetBoolean());
        Assert.True((await admin.GetFromJsonAsync<JsonElement>($"{Types}/{id}")).GetProperty("acceptanceFields").GetBoolean());
        await SetFlagsAsync(admin, id, marked: true);
        return (admin, id);
    }

    private static async Task SetFlagsAsync(HttpClient admin, Guid id, bool marked)
    {
        var type = await admin.GetFromJsonAsync<JsonElement>($"{Types}/{id}");
        var schema = JsonNode.Parse(type.GetProperty("schema").GetRawText())!.AsObject();
        var fields = schema["fields"]!.AsArray();
        foreach (var (key, title, flag) in new[] { ("act", "Акт", marked), ("test", "Протокол", marked), ("note", "Примечание", false) })
        {
            var existing = fields.FirstOrDefault(field => field?["key"]?.GetValue<string>() == key);
            if (existing is not null) fields.Remove(existing);
            fields.Add(new JsonObject { ["key"] = key, ["type"] = "string", ["title"] = title, ["forAcceptance"] = flag });
        }
        using var response = await admin.PutAsJsonAsync($"{Types}/{id}/schema", new { schema = schema.ToJsonString() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task AddFieldsAsync(HttpClient admin, Guid id, params JsonObject[] added)
    {
        var type = await admin.GetFromJsonAsync<JsonElement>($"{Types}/{id}");
        var schema = JsonNode.Parse(type.GetProperty("schema").GetRawText())!.AsObject();
        foreach (var field in added) schema["fields"]!.AsArray().Add(field);
        using var response = await admin.PutAsJsonAsync($"{Types}/{id}/schema", new { schema = schema.ToJsonString() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Guid[] ChoiceIds(JsonElement response) => response.GetProperty("items").EnumerateArray()
        .Select(item => item.GetProperty("id").GetGuid()).ToArray();

    private async Task SubmitFilledAsync(HttpClient installer, Guid id, Guid site)
    {
        using var create = await installer.PostAsJsonAsync(Reports, WithDetails(id, site, Guid.NewGuid(), new { act = "А-1", test = "П-2" }));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        using var response = await SubmissionTests.SubmitAsync(installer, $"{Reports}/{id}",
            ReviewWorld.Version(await ReportWorld.JsonAsync(create)), ReviewWorld.Evening16);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, Missing(await ReportWorld.JsonAsync(response)));
    }

    private object WithDetails(Guid id, Guid constructionId, Guid line, object details,
        string date = ReportWorld.Day16, string? reason = null) => new
    {
        id, constructionId, date, shift = "day", hours = 8, changeId = Guid.NewGuid(), reason,
        lines = new[] { new { id = line, workTypeId = World.WorkType, unitId = World.Unit, volume = 120, details } },
    };

    private static string? Code(JsonElement mark) => mark.GetProperty("code").GetString();
    private static int Missing(JsonElement report) => report.GetProperty("missingFieldsCount").GetInt32();
    private static string[] Keys(JsonElement report) => report.GetProperty("lines")[0].GetProperty("missingFields").EnumerateArray()
        .Select(field => field.GetProperty("key").GetString()!).ToArray();
}
