using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

[Collection("Integration")]
public sealed class TableSourceMetadataTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    private const string Files = "/api/datasets/files";
    private const string Sources = "/api/datasets/sources";
    private static readonly string[] Selected = ["Номер", "Итого"];

    [Fact]
    public async Task Табличный_источник_хранит_только_структуру_и_читает_живые_метаданные()
    {
        using var publisher = (await SignInAsync("Admin")).Client;
        await CreateAsync(publisher, complete: true, total: 456789.12m);
        var fileId = (await JsonAsync(await publisher.PostAsJsonAsync($"{Files}/system",
            new { scope = "System", name = $"Метаданные {Guid.NewGuid():N}" }))).GetProperty("id").GetGuid();
        var marker = SystemDataSets.TableMarker("costs.invoices", Selected);
        var direct = (await JsonAsync(await publisher.PostAsJsonAsync($"{Files}/{fileId}/sources",
            new { name = "Счета", sheetOrPath = marker }))).GetProperty("id").GetGuid();
        var bridge = await JsonAsync(await publisher.PostAsJsonAsync("/api/tables/costs.invoices/dataset",
            new { name = $"Из таблицы {Guid.NewGuid():N}", columns = Selected }));
        var bridgeId = bridge.GetProperty("sourceId").GetGuid();
        var bridgeFileId = bridge.GetProperty("fileId").GetGuid();
        var owner = Guid.NewGuid();
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = await db.DataSetSources.Where(s => s.Id == direct || s.Id == bridgeId).ToListAsync();
            Assert.Equal(2, stored.Count);
            Assert.All(stored, s =>
            {
                Assert.Equal(marker, s.SheetOrPath);
                Assert.Equal(DataSetSource.UnstoredRowCount, s.CachedRowCount);
                Assert.Null(s.CachedData);
                Assert.Equal(Selected, Schema(s.CachedSchema).EnumerateArray().Select(c => c.GetProperty("name").GetString()));
                Assert.All(Schema(s.CachedSchema).EnumerateArray(), c => Assert.Empty(Samples(c)));
            });
            var binding = DataSetBinding.For(owner, direct, "Строки", "{}");
            db.DataSetBindings.Add(binding);
            await db.SaveChangesAsync();
        }

        var role = (await JsonAsync(await publisher.PostAsJsonAsync("/api/roles", new
        {
            title = $"Читатель {Guid.NewGuid():N}", permissions = new[] { CorePermissions.DataSetsRead, "costs.invoice.read" },
        }))).GetProperty("name").GetString()!;
        var (reader, readerId) = await SignInAsync(role);
        using var readerHandle = reader;
        var before = await SourceAsync(reader, fileId, direct);
        Assert.True(before.GetProperty("cachedRowCount").GetInt32() > 0);
        Assert.NotEmpty(Samples(Column(before, "Итого")));

        await ChangeRightsAsync(publisher, reader, readerId, role, [CorePermissions.DataSetsRead, "costs.waybill.read"]);
        var narrow = await SourceAsync(reader, bridgeFileId, bridgeId);
        Assert.NotEmpty(Samples(Column(narrow, "Номер")));
        Assert.Empty(Samples(Column(narrow, "Итого")));
        Assert.NotEmpty(Column(narrow, "Итого").GetProperty("unavailable").GetString()!);
        var bindingSource = Assert.Single((await reader.GetFromJsonAsync<JsonElement>(
            $"/api/datasets/bindings?ownerId={owner}")).EnumerateArray()).GetProperty("source");
        Assert.Empty(Samples(Column(bindingSource, "Итого")));
        Assert.NotEmpty(Samples(Column(bindingSource, "Номер")));
        var mapping = await JsonAsync(await reader.PostAsJsonAsync($"{Sources}/{direct}/auto-map",
            new { fields = Selected.Select(s => new { key = s, title = s }) }));
        Assert.True(mapping.GetProperty("mapping").TryGetProperty("Номер", out _));
        Assert.False(mapping.GetProperty("mapping").TryGetProperty("Итого", out _));

        await ChangeRightsAsync(publisher, reader, readerId, role, [CorePermissions.DataSetsRead]);
        var denied = await SourceAsync(reader, fileId, direct);
        AssertNoMetadata(denied);
        var file = Assert.Single((await reader.GetFromJsonAsync<JsonElement>($"{Files}?scope=System"))
            .EnumerateArray(), f => f.GetProperty("id").GetGuid() == fileId);
        AssertNoMetadata(Assert.Single(file.GetProperty("sources").EnumerateArray(),
            s => s.GetProperty("id").GetGuid() == direct));
        AssertNoMetadata(Assert.Single((await reader.GetFromJsonAsync<JsonElement>(
            $"/api/datasets/bindings?ownerId={owner}")).EnumerateArray()).GetProperty("source"));
        var snapshot = await McpSourceAsync(reader, direct);
        Assert.Empty(snapshot.GetProperty("columns").EnumerateArray());
        AssertUnknown(snapshot, "rawRowCount");
        Assert.NotEmpty(snapshot.GetProperty("rowsError").GetString()!);
        using var preview = await reader.GetAsync($"{Sources}/{direct}/preview?maxRows=1");
        Assert.Equal(HttpStatusCode.Forbidden, preview.StatusCode);

        using var readScope = host.Services.CreateScope();
        var unchanged = await readScope.ServiceProvider.GetRequiredService<AppDbContext>()
            .DataSetSources.AsNoTracking().SingleAsync(s => s.Id == direct);
        Assert.Equal(DataSetSource.UnstoredRowCount, unchanged.CachedRowCount);
        Assert.All(Schema(unchanged.CachedSchema).EnumerateArray(), c => Assert.Empty(Samples(c)));
    }

    [Fact]
    public async Task Копия_табличного_источника_переносит_настройки_и_структуру()
    {
        using var admin = (await SignInAsync("Admin")).Client;
        const string marker = "System:Table:plan.test?columns=Название";
        const string legacySchema = """[{"name":"Название","sampleValues":["Образец"]}]""";
        var file = DataSetFile.CreateSystem("Набор из предыдущей версии", CatalogScope.System, null);
        var source = file.AddSource("Источник", marker, legacySchema, 42);
        source.SetProcessing(null, null, """[{"column":"Название","descending":false}]""");
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.DataSetFiles.Add(file);
            await db.SaveChangesAsync();
        }

        AssertNoMetadata(await SourceAsync(admin, file.Id, source.Id));
        var snapshot = await McpSourceAsync(admin, source.Id);
        AssertUnknown(snapshot, "rawRowCount");
        Assert.Empty(snapshot.GetProperty("columns").EnumerateArray());
        var dataset = await McpAsync(admin, "get_dataset", new { datasetId = file.Id });
        var summary = Assert.Single(dataset.GetProperty("sources").EnumerateArray());
        AssertUnknown(summary, "rawRowCount");
        Assert.Empty(summary.GetProperty("columns").EnumerateArray());

        using var exportScope = host.Services.CreateScope();
        var (stream, _) = await exportScope.ServiceProvider.GetRequiredService<BackupService>().ExportAsync(BackupScope.Full);
        await using var exported = stream;
        using var zip = new ZipArchive(exported, ZipArchiveMode.Read);
        BackupManifest manifest;
        using (var entry = zip.GetEntry("manifest.json")!.Open())
            manifest = (await JsonSerializer.DeserializeAsync<BackupManifest>(entry))!;
        var backup = Assert.Single(manifest.DataSetSources!, s => s.Id == source.Id);
        Assert.Equal(marker, backup.SheetOrPath);
        Assert.Equal(DataSetSource.UnstoredRowCount, backup.CachedRowCount);
        Assert.Empty(Samples(Assert.Single(Schema(backup.CachedSchema).EnumerateArray())));
        Assert.NotNull(backup.SortSpec);

        // Входной архив предыдущей версии содержит старое структурное представление источника.
        var prior = manifest with
        {
            DataSetSources = manifest.DataSetSources!.Select(s => s.Id == source.Id
                ? s with { CachedSchema = legacySchema, CachedRowCount = 42 } : s).ToArray(),
        };
        using var legacy = new MemoryStream();
        using (var edited = new ZipArchive(legacy, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in zip.Entries.Where(e => e.FullName != "manifest.json"))
            {
                using var from = entry.Open();
                using var to = edited.CreateEntry(entry.FullName).Open();
                await from.CopyToAsync(to);
            }
            using var replacement = edited.CreateEntry("manifest.json").Open();
            await JsonSerializer.SerializeAsync(replacement, prior);
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            legacy.Position = 0;
            using var restoreScope = host.Services.CreateScope();
            var report = await restoreScope.ServiceProvider.GetRequiredService<BackupService>().ImportAsync(legacy);
            Assert.True(report.Success, string.Join(Environment.NewLine, report.Warnings));
            var db = restoreScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var saved = await db.DataSetSources.AsNoTracking().SingleAsync(s => s.Id == source.Id);
            Assert.Equal(file.Id, saved.FileId);
            Assert.Equal(marker, saved.SheetOrPath);
            Assert.Equal(backup.SortSpec, saved.SortSpec);
            Assert.Equal(backup.UpdatedAt, saved.UpdatedAt);
            Assert.Equal(DataSetSource.UnstoredRowCount, saved.CachedRowCount);
            Assert.Empty(Samples(Assert.Single(Schema(saved.CachedSchema).EnumerateArray())));
        }
    }

    private async Task ChangeRightsAsync(HttpClient admin, HttpClient reader, Guid userId,
        string role, string[] permissions)
    {
        using var update = await admin.PutAsJsonAsync($"/api/roles/{role}/permissions", new { permissions });
        update.EnsureSuccessStatusCode();
        using var scope = host.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            .FindByIdAsync(userId.ToString());
        var login = await JsonAsync(await reader.PostAsJsonAsync("/api/auth/login",
            new { email = user!.Email, password = "Test#12345" }));
        reader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            login.GetProperty("accessToken").GetString());
    }

    private static async Task<JsonElement> SourceAsync(HttpClient client, Guid file, Guid source) =>
        Assert.Single((await client.GetFromJsonAsync<JsonElement>($"{Files}/{file}/sources")).EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == source);

    private static JsonElement Column(JsonElement source, string name) =>
        Assert.Single(Schema(source.GetProperty("cachedSchema").GetString()!).EnumerateArray(),
            c => c.GetProperty("name").GetString() == name);

    private static IEnumerable<string?> Samples(JsonElement column) =>
        column.GetProperty("sampleValues").EnumerateArray().Select(s => s.GetString());

    private static JsonElement Schema(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        return parsed.RootElement.Clone();
    }

    private static void AssertUnknown(JsonElement value, string name) =>
        Assert.True(!value.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null);

    private static void AssertNoMetadata(JsonElement source)
    {
        AssertUnknown(source, "cachedRowCount");
        Assert.Empty(Schema(source.GetProperty("cachedSchema").GetString()!).EnumerateArray());
    }

    private static Task<JsonElement> McpSourceAsync(HttpClient reader, Guid sourceId) =>
        McpAsync(reader, "get_source", new { sourceId });

    private static async Task<JsonElement> McpAsync(HttpClient reader, string name, object arguments)
    {
        var result = await McpTestClient.CallAsync(reader, "tools/call", new { name, arguments });
        return Schema(Assert.Single(result.GetProperty("content").EnumerateArray()).GetProperty("text").GetString()!);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
            return Schema(body);
        }
    }
}
