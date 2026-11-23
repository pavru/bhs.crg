using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Work;
using BHS.CRG.Tests.Integration;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NPOI.XSSF.UserModel;
using static BHS.CRG.Tests.Work.WorkTableWorld;

namespace BHS.CRG.Tests.Work;

[Collection(AssignmentHost.Collection)]
public class WorkTableIsolationTests(AssignmentHost host) : IAsyncLifetime
{
    private readonly WorkTableWorld world = new(host);

    public Task InitializeAsync() => world.Assignments.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Один_охват_в_таблицах_итогах_ключах_выгрузке_и_наборе()
    {
        var employeeType = await world.Assignments.DirectoryAsync();
        var siteA = await world.Assignments.SiteAsync("Стройка А");
        var siteB = await world.Assignments.SiteAsync("Стройка Б");
        var (employeeA, installer) = await world.InstallerAsync(employeeType, "Монтажник А");
        var (employeeB, _) = await world.InstallerAsync(employeeType, "Монтажник Б");
        var (_, manager) = await world.ManagerAsync(employeeType, siteA);
        var (type, unit, typeLabel) = await world.ClassifierAsync();
        var a = await world.ReportAsync(employeeA, siteA, type, unit, "Место А", 7, true, 10, 20);
        var b = await world.ReportAsync(employeeB, siteB, type, unit, "Место Б", 9, true, 100);

        var reports = await installer.GetFromJsonAsync<JsonElement>($"{Reports}?totals=Часы");
        Assert.Equal("Ваши отчёты", reports.GetProperty("boundary").GetString());
        Assert.Equal(1, reports.GetProperty("count").GetInt32());
        Assert.Equal(a.Id, Assert.Single(reports.GetProperty("keys").EnumerateArray()).GetGuid());
        Assert.Equal(7, reports.GetProperty("totals").GetProperty("Часы").GetProperty("sum").GetDecimal());

        var selectedType = JsonSerializer.Serialize(new { type = "condition", column = "ВидРаботы", op = "eq", value = typeLabel });
        var lines = await installer.GetFromJsonAsync<JsonElement>($"{Lines}?limit=1&totals=Объём&filter={Uri.EscapeDataString(selectedType)}");
        Assert.Equal(2, lines.GetProperty("count").GetInt32());
        Assert.Single(lines.GetProperty("rows").EnumerateArray());
        Assert.Equal(30, lines.GetProperty("totals").GetProperty("Объём").GetProperty("sum").GetDecimal());
        Assert.DoesNotContain(lines.GetProperty("columns").EnumerateArray(), c => c.GetProperty("key").GetString() == "Часы");

        foreach (var (path, key) in new[] { (Reports, b.Id), (Lines, b.Lines[0].Id) })
        {
            var hidden = await installer.GetAsync($"{path}?row={key}");
            var missing = await installer.GetAsync($"{path}?row={Guid.NewGuid()}");
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
            Assert.Equal(missing.StatusCode, hidden.StatusCode);
            Assert.Equal(await missing.Content.ReadAsStringAsync(), await hidden.Content.ReadAsStringAsync());
        }

        var row = await manager.GetFromJsonAsync<JsonElement>($"{Reports}?row={a.Id}");
        Assert.Equal(a.Id, Assert.Single(row.GetProperty("keys").EnumerateArray()).GetGuid());
        var columns = await manager.GetFromJsonAsync<JsonElement>($"{Reports}/columns");
        Assert.Equal(WorkRecordTypes.ReportCode, columns.GetProperty("recordType").GetString());
        var card = await manager.GetAsync($"/api/work/review/reports/{a.Id}");
        card.EnsureSuccessStatusCode();

        var export = await manager.PostAsJsonAsync($"{Lines}/export", new { columns = new[] { "Место" } });
        export.EnsureSuccessStatusCode();
        using var workbook = new XSSFWorkbook(await export.Content.ReadAsStreamAsync());
        var sheet = workbook.GetSheetAt(0);
        var text = Enumerable.Range(0, sheet.LastRowNum + 1)
            .Select(n => sheet.GetRow(n)?.GetCell(0)?.ToString()).ToList();
        Assert.Equal(2, text.Count(value => value == "Место А"));
        Assert.DoesNotContain("Место Б", text);
        Assert.Contains(text, cell => cell?.Contains("Отчёты ваших строек (1)") == true);

        foreach (var (marker, count) in new[] { (ReportsMarker, 1), (LinesMarker, 2) })
        {
            var sourceId = await SourceAsync(marker);
            var preview = await installer.GetFromJsonAsync<JsonElement>($"/api/datasets/sources/{sourceId}/preview?maxRows=20");
            Assert.Equal(count, preview.GetProperty("rows").GetArrayLength());
            Assert.Equal("Ваши отчёты", preview.GetProperty("boundary").GetString());

            var snapshot = await SnapshotAsync(installer, sourceId);
            var rows = snapshot.GetProperty("rows");
            Assert.Equal(count, rows.GetArrayLength());
            Assert.All(rows.EnumerateArray(), r => Assert.Equal("Монтажник А", r.GetProperty("Сотрудник").GetString()));
            Assert.Equal("Ваши отчёты", snapshot.GetProperty("boundary").GetString());
        }
    }

    [Fact]
    public async Task Граница_менеджера_показывает_четыре_стройки_или_причину_отсутствия_назначений()
    {
        var employeeType = await world.Assignments.DirectoryAsync();
        var sites = new List<Guid>();
        for (var n = 1; n <= 4; n++) sites.Add(await world.Assignments.SiteAsync($"Стройка {n}"));
        var (_, manager) = await world.ManagerAsync(employeeType, [.. sites]);
        var (_, unassigned) = await world.ManagerAsync(employeeType);
        foreach (var path in new[] { Reports, Lines })
        {
            var four = await manager.GetFromJsonAsync<JsonElement>(path);
            Assert.Equal("sites", four.GetProperty("boundaryOutcome").GetString());
            Assert.Contains("Отчёты ваших строек (4)", four.GetProperty("boundary").GetString());
            var empty = await unassigned.GetFromJsonAsync<JsonElement>(path);
            Assert.Equal(0, empty.GetProperty("count").GetInt32());
            Assert.Equal("not-assigned", empty.GetProperty("reason").GetString());
            Assert.Contains("Вы не назначены ни на одну стройку", empty.GetProperty("boundary").GetString());
        }

        var sourceId = await SourceAsync(ReportsMarker);
        var preview = await unassigned.GetFromJsonAsync<JsonElement>($"/api/datasets/sources/{sourceId}/preview?maxRows=20");
        Assert.Equal("not-assigned", preview.GetProperty("reason").GetString());
        var snapshot = await SnapshotAsync(unassigned, sourceId);
        Assert.Equal("not-assigned", snapshot.GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData(WorkModule.ReportRead)]
    [InlineData(WorkModule.SitesAll)]
    public async Task Право_общего_чтения_видит_отчёты_и_строки_всех_строек(string permission)
    {
        var site = await world.Assignments.SiteAsync("Любая стройка");
        var (type, unit, _) = await world.ClassifierAsync();
        var report = await world.ReportAsync(Guid.NewGuid(), site, type, unit, "Место", 7, true, 10);
        var (_, reader) = await world.Assignments.UserAsync("Читатель", permission);
        var reports = await reader.GetFromJsonAsync<JsonElement>(Reports);
        var lines = await reader.GetFromJsonAsync<JsonElement>(Lines);
        Assert.Equal(report.Id, Assert.Single(reports.GetProperty("keys").EnumerateArray()).GetGuid());
        Assert.Equal(report.Lines[0].Id, Assert.Single(lines.GetProperty("keys").EnumerateArray()).GetGuid());
        Assert.Equal("all", reports.GetProperty("boundaryOutcome").GetString());
        Assert.Equal("all", lines.GetProperty("boundaryOutcome").GetString());
    }

    [Fact]
    public async Task Право_контракта_не_открывает_таблицы_и_наборы()
    {
        const string facts = "work.facts.read";
        var catalog = host.Services.GetRequiredService<PermissionCatalog>();
        var declared = catalog.All.SingleOrDefault(p => p.Code == facts);
        var contract = declared ?? new AppPermission(facts, "Чтение факта работ", "Принятый объём", Contract: true,
            ReadAll: ReadAllMark.Out("Право другого модуля"));
        Assert.True(contract.Contract);
        var permissions = declared is null ? new PermissionCatalog([.. catalog.All, contract]) : catalog;
        await using var withContract = host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton(permissions))));
        using var engineer = withContract.CreateClient();
        var (_, signedIn) = await world.Assignments.UserAsync("Инженер", facts, CorePermissions.DataSetsRead);
        using (signedIn)
            engineer.DefaultRequestHeaders.Authorization = signedIn.DefaultRequestHeaders.Authorization;

        var access = await engineer.GetFromJsonAsync<JsonElement>("/api/account/access");
        Assert.Contains(access.GetProperty("permissions").EnumerateArray(), p => p.GetString() == facts);

        var list = await engineer.GetFromJsonAsync<JsonElement>("/api/tables");
        Assert.DoesNotContain(list.EnumerateArray(), item => item.GetProperty("module").GetString() == "work");
        foreach (var (path, marker) in new[] { (Reports, ReportsMarker), (Lines, LinesMarker) })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await engineer.GetAsync(path)).StatusCode);
            var source = await SourceAsync(marker);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await engineer.GetAsync($"/api/datasets/sources/{source}/preview?maxRows=20")).StatusCode);
        }
        var candidates = await engineer.GetFromJsonAsync<JsonElement>("/api/datasets/system-candidates?scope=System");
        Assert.DoesNotContain(candidates.EnumerateArray(), item =>
            item.GetProperty("sheetOrPath").GetString()!.StartsWith("system:table:work.", StringComparison.Ordinal));
    }

    private async Task<Guid> SourceAsync(string marker)
    {
        using var scope = host.Services.CreateScope();
        var datasets = scope.ServiceProvider.GetRequiredService<IDataSetService>();
        var file = await datasets.CreateSystemFileAsync(new CreateSystemFileInput("System", null, marker), default);
        var access = DataAccess.Of(Guid.NewGuid(), "Публикатор", ["work", WorkModule.ReportRead], ["work"]);
        return (await datasets.CreateSourceAsync(file.Id, new CreateSourceInput(marker, marker, null), access, default)).Id;
    }

    private static async Task<JsonElement> SnapshotAsync(HttpClient client, Guid sourceId)
    {
        var result = await McpTestClient.CallAsync(client, "tools/call",
            new { name = "get_rows", arguments = new { sourceId } });
        var content = Assert.Single(result.GetProperty("content").EnumerateArray());
        using var snapshot = JsonDocument.Parse(content.GetProperty("text").GetString()!);
        return snapshot.RootElement.Clone();
    }
}
