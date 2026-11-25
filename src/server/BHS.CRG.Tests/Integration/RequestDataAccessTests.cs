using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Tests.Work;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BHS.CRG.Tests.Integration;

public sealed class RequestDataAccessHost : AssignmentHost
{
    protected override string DatabaseSuffix => "request_access";
}

public sealed class RequestDataAccessTests(RequestDataAccessHost host) : IClassFixture<RequestDataAccessHost>
{
    private const string Own = "Монтажник А";
    private const string Other = "Монтажник Б";
    private const string Reports = WorkTableWorld.Reports;
    private readonly RequestDataAccessHost testHost = host;
    private readonly ReportWorld world = new(host);

    [Fact]
    public async Task Подмена_параметра_запроса_достигает_таблицы_и_MCP_при_том_же_JWT()
    {
        testHost.ReportTime = ReviewWorld.Evening16.ToUniversalTime();
        await world.SeedAsync();
        var (account, signedIn) = await world.Assignments.UserAsync(Own,
            WorkModule.ReportOwn, WorkModule.ReportRead, CorePermissions.DataSetsRead, CorePermissions.DataSetsEdit);
        using var reader = signedIn;
        var employee = await world.EmployeeAsync(Own);
        await world.Assignments.LinkAsync(employee, account);

        var (otherEmployee, signedInWithoutDataSets) = await world.InstallerAsync(Other);
        using var other = signedInWithoutDataSets;
        var ownReport = await SubmitAsync(reader, employee, world.SiteA);
        var otherReport = await SubmitAsync(other, otherEmployee, world.SiteB);
        var (datasetId, sourceId) = await PublishAsync(reader);

        var fullBoundary = await CheckRowsAsync(reader, sourceId, [ownReport, otherReport], [Own, Other]);
        Assert.StartsWith("Все отчёты", fullBoundary);
        await CheckSnapshotMetadataAsync(reader, datasetId, sourceId, 2);
        await CheckMcpGateAsync(other, sourceId);

        await using (var overridden = testHost.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IRequestDataAccessResolver>();
            services.AddSingleton<IRequestDataAccessResolver>(sp =>
                new OwnReportsOnly(sp.GetRequiredService<DataAccessResolver>()));
        })))
        {
            using var narrowed = overridden.CreateClient();
            narrowed.DefaultRequestHeaders.Authorization = reader.DefaultRequestHeaders.Authorization;
            using var denied = overridden.CreateClient();
            denied.DefaultRequestHeaders.Authorization = other.DefaultRequestHeaders.Authorization;
            Assert.Same(overridden.Services.GetRequiredService<DataAccessResolver>(),
                overridden.Services.GetRequiredService<IDataAccessResolver>());

            Assert.Equal("Ваши отчёты", await CheckRowsAsync(narrowed, sourceId, [ownReport], [Own]));
            await CheckSnapshotMetadataAsync(narrowed, datasetId, sourceId, 1);
            await CheckMcpGateAsync(denied, sourceId);
        }

        Assert.Equal(fullBoundary, await CheckRowsAsync(reader, sourceId, [ownReport, otherReport], [Own, Other]));
    }

    private async Task<Guid> SubmitAsync(HttpClient client, Guid employee, Guid site)
    {
        await world.Assignments.AssignedAsync(employee, site, AssignmentKind.Installer, "2026-10-01", "2026-11-30");
        var id = Guid.NewGuid();
        var draft = await CrewWorld.CreateAsync(client, world.Draft(id, site));
        await CrewWorld.SubmitAsync(client, id, draft);
        return id;
    }

    private static async Task<(Guid Dataset, Guid Source)> PublishAsync(HttpClient client)
    {
        using var file = await client.PostAsJsonAsync("/api/datasets/files/system",
            new { scope = "System", name = "Отчёты проверки доступа" });
        file.EnsureSuccessStatusCode();
        var datasetId = (await file.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var source = await client.PostAsJsonAsync($"/api/datasets/files/{datasetId}/sources",
            new { name = "Отчёты", sheetOrPath = WorkTableWorld.ReportsMarker });
        source.EnsureSuccessStatusCode();
        var sourceId = (await source.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        return (datasetId, sourceId);
    }

    private static async Task<string> CheckRowsAsync(
        HttpClient client, Guid sourceId, Guid[] expectedKeys, string[] expectedEmployees)
    {
        using var response = await client.PostAsJsonAsync($"{Reports}/query", new { columns = new[] { "Сотрудник" } });
        response.EnsureSuccessStatusCode();
        var table = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedKeys.Order(), table.GetProperty("keys").EnumerateArray().Select(key => key.GetGuid()).Order());
        Assert.Equal(expectedKeys.Length, table.GetProperty("count").GetInt32());
        var boundary = table.GetProperty("boundary").GetString()!;

        var snapshot = await CallToolAsync(client, "get_rows", new { sourceId });
        Assert.Equal(expectedEmployees.Length, snapshot.GetProperty("totalRows").GetInt32());
        Assert.Equal(expectedEmployees.Order(), snapshot.GetProperty("rows").EnumerateArray()
            .Select(row => row.GetProperty("Сотрудник").GetString()).Order());
        Assert.Equal(boundary, snapshot.GetProperty("boundary").GetString());
        return boundary;
    }

    private static async Task CheckSnapshotMetadataAsync(HttpClient client, Guid datasetId, Guid sourceId, int count)
    {
        var catalog = await CallToolAsync(client, "list_datasets", new { scope = "System" });
        Assert.Contains(catalog.GetProperty("items").EnumerateArray(), item => item.GetProperty("id").GetGuid() == datasetId);

        var source = await CallToolAsync(client, "get_source", new { sourceId });
        Assert.Equal(count, source.GetProperty("rowCount").GetInt32());
        var sourceResource = await ReadResourceAsync(client, $"bhs://source/{sourceId}");
        Assert.Equal(count, sourceResource.GetProperty("rowCount").GetInt32());

        var dataset = await CallToolAsync(client, "get_dataset", new { datasetId });
        var datasetResource = await ReadResourceAsync(client, $"bhs://dataset/{datasetId}");
        foreach (var result in new[] { dataset, datasetResource })
        {
            var entry = Assert.Single(result.GetProperty("sources").EnumerateArray());
            Assert.Equal(sourceId, entry.GetProperty("id").GetGuid());
            Assert.Equal(count, entry.GetProperty("rawRowCount").GetInt32());
        }
    }

    private static async Task CheckMcpGateAsync(HttpClient client, Guid sourceId)
    {
        var denied = await McpTestClient.TryCallAsync(client, "tools/call",
            new { name = "get_rows", arguments = new { sourceId } });
        Assert.True(denied.TryGetProperty("error", out _), "MCP должен вернуть отказ: " + denied);
    }

    private static async Task<JsonElement> CallToolAsync(HttpClient client, string name, object arguments)
    {
        var result = await McpTestClient.CallAsync(client, "tools/call", new { name, arguments });
        return ReadText(result, "content");
    }

    private static async Task<JsonElement> ReadResourceAsync(HttpClient client, string uri)
    {
        var result = await McpTestClient.CallAsync(client, "resources/read", new { uri });
        return ReadText(result, "contents");
    }

    private static JsonElement ReadText(JsonElement result, string property)
    {
        var entry = Assert.Single(result.GetProperty(property).EnumerateArray());
        using var document = JsonDocument.Parse(entry.GetProperty("text").GetString()!);
        return document.RootElement.Clone();
    }

    private sealed class OwnReportsOnly(DataAccessResolver original) : IRequestDataAccessResolver
    {
        public async Task<DataAccess> ForAsync(ClaimsPrincipal principal, CancellationToken ct)
        {
            var access = await original.ForAsync(principal, ct);
            return DataAccess.Of(access.UserId!.Value, access.Who,
                access.Keys.Where(key => key != WorkModule.ReportRead), access.EnabledModules);
        }
    }
}
