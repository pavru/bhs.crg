using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Backup;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Work;
using BHS.CRG.Tests.Work;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

[Collection(AssignmentHost.Collection)]
public class IsolatedSourceStorageTests(AssignmentHost host)
{
    private const string Files = "/api/datasets/files";
    private const string Sources = "/api/datasets/sources";

    [Fact]
    public async Task Схема_в_базе_и_копии_не_зависит_от_читающего()
    {
        var world = new ReviewWorld(host);
        await world.SeedAsync();
        var a = $"Монтажник А {Guid.NewGuid():N}";
        var b = $"Монтажник Б {Guid.NewGuid():N}";
        var (_, installerA) = await world.Reports.InstallerAsync(a);
        var (_, installerB) = await world.Reports.InstallerAsync(b);
        using var ownerA = installerA;
        using var ownerB = installerB;
        var (_, managerA) = await world.Reports.ManagerAsync("Менеджер А", world.SiteA, CorePermissions.DataSetsRead);
        var (_, managerB) = await world.Reports.ManagerAsync("Менеджер Б", world.SiteB, CorePermissions.DataSetsRead);
        using var readerA = managerA;
        using var readerB = managerB;
        await world.SubmittedAsync(ownerA, Guid.NewGuid(), world.SiteA);
        await world.SubmittedAsync(ownerB, Guid.NewGuid(), world.SiteB);
        var (_, publisher) = await world.Reports.Assignments.UserAsync("Публикатор",
            WorkModule.ReportRead, CorePermissions.DataSetsEdit, CorePermissions.DataSetsRead);
        using var editor = publisher;
        var (_, coreOnly) = await world.Reports.Assignments.UserAsync("Читатель ядра", CorePermissions.DataSetsRead);
        using var withoutWork = coreOnly;

        var file = await ReadAsync(await editor.PostAsJsonAsync($"{Files}/system",
            new { scope = "System", name = "Системный набор Work-30" }));
        var fileId = file.GetProperty("id").GetGuid();
        var sourceIds = new List<Guid>();
        foreach (var (table, marker) in new[]
        {
            (WorkTableWorld.Reports, WorkTableWorld.ReportsMarker),
            (WorkTableWorld.Lines, WorkTableWorld.LinesMarker),
        })
        {
            var source = await ReadAsync(await editor.PostAsJsonAsync($"{Files}/{fileId}/sources",
                new { name = marker, sheetOrPath = marker }));
            var directId = source.GetProperty("id").GetGuid();
            var bridge = await ReadAsync(await editor.PostAsJsonAsync($"{table}/dataset",
                new { name = $"Копия {marker}", columns = new[] { "Сотрудник" } }));
            var bridgeFile = bridge.GetProperty("fileId").GetGuid();
            var bridgeId = bridge.GetProperty("sourceId").GetGuid();
            sourceIds.AddRange([directId, bridgeId]);

            var version = source.GetProperty("processingVersion").GetString();
            for (var i = 0; i < 2; i++)
            {
                var saved = await ReadAsync(await editor.PutAsJsonAsync($"{Sources}/{directId}/processing",
                    new { ifMatch = version, sortSpec = Array.Empty<object>() }));
                version = saved.GetProperty("processingVersion").GetString();
                AssertMissingCount(saved, "cachedRowCount");
            }

            await CheckReadingAsync(readerA, fileId, directId, a, b);
            await CheckReadingAsync(readerB, bridgeFile, bridgeId, b, a);
            var withheld = await SourceMetadataAsync(withoutWork, fileId, directId);
            AssertMissingCount(withheld, "cachedRowCount");
            var snapshot = await McpSourceAsync(withoutWork, directId);
            AssertMissingCount(snapshot, "rawRowCount");
        }

        var ordinaryId = await CreateCsvAsync(editor);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sources = await db.DataSetSources.AsNoTracking().Where(s => sourceIds.Contains(s.Id)).ToListAsync();
        Assert.Equal(4, sources.Count);
        Assert.All(sources, AssertSafeStorage);
        var csv = await db.DataSetSources.AsNoTracking().SingleAsync(s => s.Id == ordinaryId);
        Assert.Equal(1, csv.CachedRowCount);
        Assert.Contains("Обычный файл", csv.CachedSchema);

        var backup = scope.ServiceProvider.GetRequiredService<BackupService>();
        var (stream, _) = await backup.ExportAsync(BackupScope.Full);
        await using var copy = stream;
        using (var zip = new ZipArchive(copy, ZipArchiveMode.Read, leaveOpen: true))
        {
            await using var json = zip.GetEntry("manifest.json")!.Open();
            var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var stored = manifest!.DataSetSources!.Where(s => sourceIds.Contains(s.Id)).ToArray();
            Assert.Equal(4, stored.Length);
            Assert.All(stored, s =>
            {
                Assert.Equal(DataSetSource.UnstoredRowCount, s.CachedRowCount);
                Assert.Null(s.CachedData);
                Assert.DoesNotContain(a, s.CachedSchema);
                Assert.DoesNotContain(b, s.CachedSchema);
            });
        }
        copy.Position = 0;
        var restored = await backup.ImportAsync(copy);
        Assert.True(restored.Success, string.Join(Environment.NewLine, restored.Warnings));
        Assert.All(await db.DataSetSources.AsNoTracking().Where(s => sourceIds.Contains(s.Id)).ToListAsync(),
            AssertSafeStorage);
        await CheckReadingAsync(readerA, fileId, sourceIds[0], a, b);
    }

    private static async Task<Guid> CreateCsvAsync(HttpClient client)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("System"), "scope");
        form.Add(new StringContent("Название\nОбычный файл\n"), "file", "work30-control.csv");
        var file = await ReadAsync(await client.PostAsync(Files, form));
        var id = file.GetProperty("id").GetGuid();
        var candidates = await client.GetFromJsonAsync<JsonElement>($"{Files}/{id}/source-candidates");
        var marker = Assert.Single(candidates.EnumerateArray()).GetProperty("sheetOrPath").GetString();
        var source = await ReadAsync(await client.PostAsJsonAsync($"{Files}/{id}/sources",
            new { name = "Контроль", sheetOrPath = marker }));
        return source.GetProperty("id").GetGuid();
    }

    private static void AssertSafeStorage(DataSetSource source)
    {
        Assert.Equal(DataSetSource.UnstoredRowCount, source.CachedRowCount);
        Assert.Null(source.CachedData);
        using var schema = JsonDocument.Parse(source.CachedSchema);
        Assert.NotEmpty(schema.RootElement.EnumerateArray());
        Assert.All(schema.RootElement.EnumerateArray(),
            col => Assert.Empty(col.GetProperty("sampleValues").EnumerateArray()));
    }

    private static async Task CheckReadingAsync(HttpClient user, Guid file, Guid source, string own, string other)
    {
        var metadata = await SourceMetadataAsync(user, file, source);
        Assert.Equal(1, metadata.GetProperty("cachedRowCount").GetInt32());
        var preview = await user.GetFromJsonAsync<JsonElement>($"{Sources}/{source}/preview?maxRows=20");
        var row = Assert.Single(preview.GetProperty("rows").EnumerateArray());
        var cells = row.EnumerateArray().Where(cell => cell.ValueKind == JsonValueKind.String)
            .Select(cell => cell.GetString());
        Assert.Contains(own, cells);
        Assert.DoesNotContain(other, cells);
        var snapshot = await McpSourceAsync(user, source);
        Assert.Equal(1, snapshot.GetProperty("rawRowCount").GetInt32());
        Assert.Equal(1, snapshot.GetProperty("rowCount").GetInt32());
        var samples = snapshot.GetProperty("columns").EnumerateArray().SelectMany(
            col => col.GetProperty("sampleValues").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains(own, samples);
        Assert.DoesNotContain(other, samples);
    }

    private static async Task<JsonElement> SourceMetadataAsync(HttpClient user, Guid file, Guid source)
    {
        var entries = await user.GetFromJsonAsync<JsonElement>($"{Files}/{file}/sources");
        return Assert.Single(entries.EnumerateArray(), entry => entry.GetProperty("id").GetGuid() == source);
    }

    private static void AssertMissingCount(JsonElement source, string property) =>
        Assert.True(!source.TryGetProperty(property, out var count) || count.ValueKind == JsonValueKind.Null);

    private static async Task<JsonElement> McpSourceAsync(HttpClient user, Guid sourceId)
    {
        var result = await McpTestClient.CallAsync(user, "tools/call",
            new { name = "get_source", arguments = new { sourceId } });
        var text = Assert.Single(result.GetProperty("content").EnumerateArray()).GetProperty("text").GetString();
        using var json = JsonDocument.Parse(text!);
        return json.RootElement.Clone();
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
            using var json = JsonDocument.Parse(body);
            return json.RootElement.Clone();
        }
    }
}
