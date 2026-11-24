using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Modules;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using BHS.CRG.Tests.Work;
using Microsoft.Extensions.DependencyInjection;
using NPOI.XSSF.UserModel;

namespace BHS.CRG.Tests.Integration;

public sealed class WorkIsolationHost : AssignmentHost
{
    protected override string DatabaseSuffix => "workiso";
}

public sealed partial class WorkIsolationViolationTests(WorkIsolationHost host) : IClassFixture<WorkIsolationHost>
{
    private const string Day = ReportWorld.Day16;
    private readonly WorkIsolationHost testHost = host;
    private readonly ReportWorld world = new(host);
    private readonly string marker = $"Work25-{Guid.NewGuid():N}";
    private readonly Guid reportA = Guid.NewGuid();
    private readonly Guid reportB = Guid.NewGuid();
    private readonly Guid lineA = Guid.NewGuid();
    private readonly Guid lineB = Guid.NewGuid();
    private readonly Guid photoB = Guid.NewGuid();
    private readonly Guid retainedPhotoB = Guid.NewGuid();
    private Guid employeeB;
    private Guid groupB;

    [Fact]
    public async Task Сегодня_вычисляется_по_поясу_стройки_на_границе_суток()
    {
        await world.Assignments.ResetAsync();
        var directory = await world.Assignments.DirectoryAsync();
        var site = await world.Assignments.SiteAsync("Восточная стройка", "Asia/Vladivostok");
        var employee = await world.Assignments.EmployeeAsync(directory, "Монтажник");
        var (account, client) = await world.Assignments.UserAsync("Монтажник", WorkModule.ReportOwn);
        using (client)
        {
            await world.Assignments.LinkAsync(employee, account);
            await world.Assignments.AssignedAsync(employee, site, AssignmentKind.Installer, "2026-10-01", "2026-11-30");

            // Часы порта в AssignmentHost стоят на 31 октября по Москве и 1 ноября по Владивостоку.
            var access = await client.GetFromJsonAsync<JsonElement>("/api/account/access");
            Assert.Equal("Europe/Moscow", access.GetProperty("company").GetProperty("timeZone").GetString());
            var sites = await client.GetFromJsonAsync<JsonElement>("/api/work/sites/mine");
            var row = Assert.Single(sites.GetProperty("items").EnumerateArray());
            Assert.Equal(site, row.GetProperty("id").GetGuid());
            Assert.Equal("Asia/Vladivostok", row.GetProperty("timeZoneId").GetString());
            Assert.Equal("2026-11-01", row.GetProperty("today").GetString());
        }
    }

    [Fact]
    public async Task Отчёт_виден_только_в_охвате_читающей_роли()
    {
        var (installer, owner, manager, unassigned, executive) = await SeedAsync();
        using (installer)
        using (owner)
        using (manager)
        using (unassigned)
        using (executive)
        {
            await CheckWorkRoutesAsync(installer, owner, manager, unassigned, executive);
            await CheckTablesAsync(installer, unassigned, executive);
            await CheckDataSetsAsync(installer, unassigned, executive);
            foreach (var client in new[] { installer, unassigned, executive })
            {
                await CheckResponseAsync(client, "/api/notifications", HttpStatusCode.OK);
                await CheckResponseAsync(client, "/api/activity", HttpStatusCode.Forbidden);
            }
            await CheckCombinedRoleAsync();
        }
    }

    private async Task CheckCombinedRoleAsync()
    {
        var (employee, client) = await ReaderAsync("Монтажник и менеджер", WorkModule.ReportOwn, WorkModule.ReportReview);
        using (client)
        {
            await world.Assignments.AssignedAsync(employee, world.SiteA, AssignmentKind.Manager, "2026-10-01", null);
            var ownOutside = Guid.NewGuid();
            var draft = await CrewWorld.CreateAsync(client, world.Draft(ownOutside, world.SiteB));
            await CrewWorld.SubmitAsync(client, ownOutside, draft);
            var table = await CheckTableAsync(client, WorkTableWorld.Reports, 2, "own-and-sites", "Часы", null);
            Assert.Equal(new[] { reportA, ownOutside }.Order(), table.GetProperty("keys").EnumerateArray().Select(value => value.GetGuid()).Order());
            await CheckResponseAsync(client, $"/api/work/review/reports/{ownOutside}", HttpStatusCode.OK, ownOutside.ToString());
            await CheckResponseAsync(client, $"/api/work/summary?from={Day}&to={Day}&construction={world.SiteB}",
                HttpStatusCode.OK, ownOutside.ToString());
            await CheckResponseAsync(client, $"/api/work/review/reports/{reportB}", HttpStatusCode.NotFound);
        }
    }

    private async Task<(HttpClient Installer, HttpClient Owner, HttpClient Manager, HttpClient Unassigned, HttpClient Executive)> SeedAsync()
    {
        host.ReportTime = ReviewWorld.Evening16.ToUniversalTime();
        await world.SeedAsync();
        var material = await SeedReportFieldsAsync();
        var (installerId, installer) = await ReaderAsync("Монтажник А", WorkModule.ReportOwn);
        var (ownerId, owner) = await ReaderAsync("Монтажник Б", WorkModule.ReportOwn);
        employeeB = ownerId;
        var (_, manager) = await world.ManagerAsync("Назначенный менеджер", world.SiteB, WorkModule.Assign);
        var (_, unassigned) = await ReaderAsync("Менеджер без назначений", WorkModule.ReportReview, WorkModule.Assign);
        var (_, executive) = await world.Assignments.UserAsync("Руководитель", WorkModule.ReportRead, CorePermissions.DataSetsRead);
        await world.Assignments.AssignedAsync(ownerId, world.SiteB, AssignmentKind.Installer, "2026-10-01", "2026-11-30");
        await world.Assignments.AssignedAsync(installerId, world.SiteA, AssignmentKind.Installer, "2026-10-01", "2026-11-30");
        var own = await CrewWorld.CreateAsync(installer, world.Draft(reportA, world.SiteA, lineId: lineA, location: "Участок А", volume: 7));
        await CrewWorld.SubmitAsync(installer, reportA, own);
        var draft = await CrewWorld.CreateAsync(owner, new
        {
            id = reportB, constructionId = world.SiteB, date = Day, shift = "day", hours = 8m, note = marker,
            lines = new[]
            {
                new { id = lineB, workTypeId = world.WorkType, unitId = world.Unit, volume = 100m, isCrew = true,
                    crewRole = "lead", location = marker, comment = marker, details = new { work25 = marker },
                    materials = new[] { new { nomenclatureId = material, note = marker } },
                    photos = new[] { new { id = photoB, originalName = $"{marker}-1.jpg" },
                        new { id = retainedPhotoB, originalName = $"{marker}-2.jpg" } } },
            },
        });
        await CrewWorld.SubmitAsync(owner, reportB, draft);
        using var upload = await PhotoWorld.UploadAsync(owner, reportB, lineB, photoB, PhotoWorld.Shot());
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        using var retained = await PhotoWorld.UploadAsync(owner, reportB, lineB, retainedPhotoB, PhotoWorld.Shot());
        Assert.Equal(HttpStatusCode.OK, retained.StatusCode);
        var crew = await executive.GetFromJsonAsync<JsonElement>($"{CrewWorld.Path}?constructionId={world.SiteB}");
        groupB = Assert.Single(crew.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid();
        return (installer, owner, manager, unassigned, executive);
    }

    private async Task<Guid> SeedReportFieldsAsync()
    {
        using var scope = testHost.Services.CreateScope();
        await scope.ServiceProvider.ProjectModuleTypesAsync();
        var (_, typeEditor) = await world.Assignments.UserAsync("Редактор типов", CorePermissions.TypesRead, CorePermissions.TypesEdit);
        using (typeEditor)
        {
            var types = await typeEditor.GetFromJsonAsync<JsonElement>("/api/document-types");
            var type = Assert.Single(types.EnumerateArray(), item => item.GetProperty("code").GetString() == WorkRecordTypes.LineDetailsCode);
            var id = type.GetProperty("id").GetGuid();
            var details = await typeEditor.GetFromJsonAsync<JsonElement>($"/api/document-types/{id}");
            var schema = JsonNode.Parse(details.GetProperty("schema").GetRawText())!.AsObject();
            schema["fields"]!.AsArray().Add(new JsonObject { ["key"] = "work25", ["type"] = "string", ["title"] = "Сведения посева" });
            using var update = await typeEditor.PutAsJsonAsync($"/api/document-types/{id}/schema", new { schema = schema.ToJsonString() });
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        }

        var seed = new WorkTypeSeed(testHost);
        var directory = await seed.TypeAsync(TestDirectories.NomenclatureCode, "Номенклатура");
        await TestDirectories.AssignRolesAsync(scope.ServiceProvider);
        return (await seed.RecordAsync(directory.Id, "Материал посева")).Id;
    }

    private async Task<(Guid Employee, HttpClient Client)> ReaderAsync(string name, params string[] permissions)
    {
        var employee = await world.EmployeeAsync(name);
        var (user, client) = await world.Assignments.UserAsync(name, [.. permissions, CorePermissions.DataSetsRead]);
        await world.Assignments.LinkAsync(employee, user);
        return (employee, client);
    }

    private async Task CheckTablesAsync(HttpClient installer, HttpClient unassigned, HttpClient executive)
    {
        foreach (var client in new[] { installer, unassigned, executive })
            await CheckResponseAsync(client, "/api/tables", HttpStatusCode.OK, "work.reports");
        foreach (var (table, total, ownValue, fullValue, ownKey, foreignKey, column, signal) in new[]
        {
            (WorkTableWorld.Reports, "Часы", 8, 16, reportA, reportB, "Сотрудник", "Монтажник Б"),
            (WorkTableWorld.Lines, "Объём", 7, 107, lineA, lineB, "Место", marker),
        })
        {
            var path = $"{table}?totals={Uri.EscapeDataString(total)}";
            var own = await CheckTableAsync(installer, path, 1, "own", total, ownValue);
            Assert.Equal(ownKey, Assert.Single(own.GetProperty("keys").EnumerateArray()).GetGuid());
            AssertReportNotDisclosed(own.GetRawText(), path);
            var none = await CheckTableAsync(unassigned, path, 0, "not-assigned", total, null);
            Assert.Equal("not-assigned", none.GetProperty("reason").GetString());
            Assert.Empty(none.GetProperty("keys").EnumerateArray());
            AssertReportNotDisclosed(none.GetRawText(), path);
            var all = await CheckTableAsync(executive, path, 2, "all", total, fullValue);
            Assert.Equal(new[] { ownKey, foreignKey }.Order(), all.GetProperty("keys").EnumerateArray().Select(value => value.GetGuid()).Order());

            await CheckResponseAsync(installer, $"{table}?row={foreignKey}", HttpStatusCode.NotFound);
            await CheckResponseAsync(unassigned, $"{table}?row={foreignKey}", HttpStatusCode.NotFound);
            await CheckResponseAsync(executive, $"{table}?row={foreignKey}", HttpStatusCode.OK,
                foreignKey.ToString(), canReadReport: true);
            await CheckQueryAndExportAsync(table, column, signal, foreignKey, installer, unassigned, executive);
        }
    }

    private async Task CheckQueryAndExportAsync(string table, string column, string signal, Guid foreignKey,
        HttpClient installer, HttpClient unassigned, HttpClient executive)
    {
        var filter = JsonSerializer.Serialize(new { type = "condition", column, op = "contains", value = signal });
        foreach (var (client, count) in new[] { (installer, 0), (unassigned, 0), (executive, 1) })
        {
            using var query = await client.PostAsJsonAsync($"{table}/query", new { filter });
            var body = await ExpectJsonAsync(query, $"{table}/query");
            Assert.Equal(count, body.GetProperty("count").GetInt32());
            var keys = body.GetProperty("keys").EnumerateArray();
            if (count == 1)
                Assert.Equal(foreignKey, Assert.Single(keys).GetGuid());
            else
                AssertReportNotDisclosed(body.GetRawText(), $"{table}/query");

            using var export = await client.PostAsJsonAsync($"{table}/export", new { columns = new[] { column } });
            Assert.Equal(HttpStatusCode.OK, export.StatusCode);
            using var workbook = new XSSFWorkbook(await export.Content.ReadAsStreamAsync());
            var sheet = workbook.GetSheetAt(0);
            Assert.Equal(count, Enumerable.Range(0, sheet.LastRowNum + 1)
                .Count(row => sheet.GetRow(row)?.GetCell(0)?.ToString() == signal));
        }
    }

    private static async Task<JsonElement> CheckTableAsync(HttpClient client, string path, int count, string boundary,
        string total, decimal? sum)
    {
        using var response = await client.GetAsync(path);
        var json = await ExpectJsonAsync(response, path);
        Assert.Equal(boundary, json.GetProperty("boundaryOutcome").GetString());
        Assert.Equal(count, json.GetProperty("count").GetInt32());
        if (sum is { } value) Assert.Equal(value, json.GetProperty("totals").GetProperty(total).GetProperty("sum").GetDecimal());
        return json;
    }

    private async Task CheckDataSetsAsync(HttpClient installer, HttpClient unassigned, HttpClient executive)
    {
        var (_, publisher) = await world.Assignments.UserAsync("Публикатор", WorkModule.ReportRead, CorePermissions.DataSetsEdit);
        using var publish = publisher;
        foreach (var (table, key) in new[]
        {
            (WorkTableWorld.Reports, WorkTableWorld.ReportsMarker),
            (WorkTableWorld.Lines, WorkTableWorld.LinesMarker),
        })
        {
            using var published = await publish.PostAsJsonAsync($"{table}/dataset", new { name = key });
            var reference = await ExpectJsonAsync(published, $"{table}/dataset");
            var fileId = reference.GetProperty("fileId").GetGuid();
            var sourceId = reference.GetProperty("sourceId").GetGuid();
            var (column, signal) = key == WorkTableWorld.LinesMarker ? ("Место", marker) : ("Сотрудник", "Монтажник Б");
            foreach (var (client, count) in new[] { (installer, 1), (unassigned, 0), (executive, 2) })
            {
                await CheckSourceMetadataAsync(client, fileId, sourceId, key, count, column, signal);
                var preview = $"/api/datasets/sources/{sourceId}/preview?maxRows=20";
                using var response = await client.GetAsync(preview);
                var rows = (await ExpectJsonAsync(response, preview)).GetProperty("rows");
                Assert.Equal(count, rows.GetArrayLength());
                Assert.Equal(count == 2, rows.EnumerateArray().Any(row => row.EnumerateArray()
                    .Any(cell => cell.ValueKind == JsonValueKind.String && cell.GetString() == signal)));
                await CheckResponseAsync(client, $"/api/datasets/sources/{sourceId}/export?format=csv", HttpStatusCode.OK,
                    count == 2 ? signal : null, canReadReport: count == 2);

                var canReadReport = count == 2;
                await McpAsync(client, "list_datasets", new { scope = "System" }, fileId.ToString(), canReadReport);
                var dataset = await McpAsync(client, "get_dataset", new { datasetId = fileId }, sourceId.ToString(), canReadReport);
                var source = Assert.Single(dataset.GetProperty("sources").EnumerateArray(), row => row.GetProperty("id").GetGuid() == sourceId);
                Assert.Equal(count, source.GetProperty("rawRowCount").GetInt32());
                var detail = await McpAsync(client, "get_source", new { sourceId }, sourceId.ToString(), canReadReport);
                Assert.Equal(count, detail.GetProperty("rowCount").GetInt32());
                Assert.Equal(count, detail.GetProperty("rawRowCount").GetInt32());
                CheckSourceSamples(detail.GetProperty("columns"), column, signal, canReadReport);
                var snapshot = await McpAsync(client, "get_rows", new { sourceId }, "totalRows", canReadReport);
                Assert.Equal(count, snapshot.GetProperty("totalRows").GetInt32());
                Assert.Equal(count == 2, snapshot.GetProperty("rows").EnumerateArray().Any(row => row.EnumerateObject()
                    .Any(cell => cell.Value.ValueKind == JsonValueKind.String && cell.Value.GetString() == signal)));
            }
        }
    }

    private async Task CheckSourceMetadataAsync(HttpClient client, Guid fileId, Guid sourceId, string key, int count,
        string column, string signal)
    {
        const string list = "/api/datasets/files?scope=System";
        var files = await ReadMetadataAsync(list);
        var file = Assert.Single(files.EnumerateArray(), row => row.GetProperty("id").GetGuid() == fileId);
        AssertSourceCount(file.GetProperty("sources"));
        AssertSourceCount(await ReadMetadataAsync($"/api/datasets/files/{fileId}/sources"));

        foreach (var path in new[] { "/api/datasets/system-candidates?scope=System", $"/api/datasets/files/{fileId}/source-candidates" })
        {
            var candidates = await ReadMetadataAsync(path);
            var selected = Assert.Single(candidates.EnumerateArray(), row => row.GetProperty("sheetOrPath").GetString() == key);
            Assert.Equal(count, selected.GetProperty("rowCount").GetInt32());
        }

        void AssertSourceCount(JsonElement entries)
        {
            var source = Assert.Single(entries.EnumerateArray(), row => row.GetProperty("id").GetGuid() == sourceId);
            Assert.Equal(count, source.GetProperty("cachedRowCount").GetInt32());
            using var schema = JsonDocument.Parse(source.GetProperty("cachedSchema").GetString()!);
            CheckSourceSamples(schema.RootElement, column, signal, count == 2);
        }

        async Task<JsonElement> ReadMetadataAsync(string path)
        {
            using var response = await client.GetAsync(path);
            var json = await ExpectJsonAsync(response, path);
            if (count != 2) AssertReportNotDisclosed(json.GetRawText(), path);
            return json;
        }
    }

    private static void CheckSourceSamples(JsonElement columns, string key, string signal, bool canReadReport)
    {
        var column = Assert.Single(columns.EnumerateArray(), entry => entry.GetProperty("name").GetString() == key);
        var values = column.GetProperty("sampleValues").EnumerateArray().Select(value => value.GetString());
        Assert.Equal(canReadReport, values.Contains(signal, StringComparer.Ordinal));
    }

    private async Task<JsonElement> McpAsync(HttpClient client, string name, object arguments, string signal, bool canReadReport)
    {
        var result = await McpTestClient.CallAsync(client, "tools/call", new { name, arguments });
        var content = Assert.Single(result.GetProperty("content").EnumerateArray()).GetProperty("text").GetString()!;
        Assert.Contains(signal, content, StringComparison.Ordinal);
        if (!canReadReport) AssertReportNotDisclosed(content, name);
        using var document = JsonDocument.Parse(content);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> ExpectJsonAsync(HttpResponseMessage response, string path)
    {
        var content = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {(int)response.StatusCode}, {content}");
        using var document = JsonDocument.Parse(content);
        return document.RootElement.Clone();
    }
}
