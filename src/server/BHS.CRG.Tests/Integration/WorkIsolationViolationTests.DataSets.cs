using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Tests.Work;
using CsvHelper;

namespace BHS.CRG.Tests.Integration;

internal sealed partial class WorkIsolationScenario
{
    private const string FileList = "/api/datasets/files?scope=System";
    private const string SystemCandidates = "/api/datasets/system-candidates?scope=System";
    private const string FileSources = "/api/datasets/files/{fileId}/sources";
    private const string FileCandidates = "/api/datasets/files/{fileId}/source-candidates";
    private const string SourcePreview = "/api/datasets/sources/{sourceId}/preview";
    private const string SourceCsv = "/api/datasets/sources/{sourceId}/export?format=csv";
    private const string SystemFileName = "Данные системы";

    private static IEnumerable<string> DataSetProbeNames()
    {
        foreach (var table in new[] { WorkTableWorld.Reports, WorkTableWorld.Lines })
        {
            yield return DataSetProbeName($"GET {FileList}", table);
            yield return DataSetProbeName($"GET {SystemCandidates}", table);
            yield return DataSetProbeName($"GET {FileSources}", table);
            yield return DataSetProbeName($"GET {FileCandidates}", table);
            yield return DataSetProbeName($"GET {SourcePreview}", table);
            yield return DataSetProbeName($"GET {SourceCsv}", table);
            yield return DataSetProbeName("MCP list_datasets", table);
            yield return DataSetProbeName("MCP get_dataset", table);
            yield return DataSetProbeName("MCP get_source", table);
            yield return DataSetProbeName("MCP get_rows", table);
            yield return DataSetProbeName("MCP resources/read bhs://dataset/{datasetId}", table);
            yield return DataSetProbeName("MCP resources/read bhs://source/{sourceId}", table);
        }
    }

    private static string DataSetProbeName(string call, string table) => $"{call} ({table.Split('/')[^1]})";

    private async Task CheckDataSetsAsync(HttpClient installer, HttpClient unassigned, HttpClient executive)
    {
        var catalog = new ProbeCatalog();
        RegisterDataSetProbes(catalog, SnapshotReaders(installer, unassigned, executive, expanded: false), await PublishSourcesAsync());
        await catalog.RunAsync(DataSetProbeNames());
    }

    private void RegisterDataSetProbes(ProbeCatalog catalog, SnapshotReader[] readers, PublishedSource[] sources)
    {
        var tables = Tables().ToDictionary(table => table.Path);
        foreach (var source in sources)
        {
            var table = tables[source.Table];
            Add($"GET {FileList}", "sources[].cachedRowCount, cachedSchema", reader =>
                ReadDataSetAsync(reader, FileList, body =>
                {
                    var file = Assert.Single(body.EnumerateArray(), row => row.GetProperty("id").GetGuid() == source.FileId);
                    Assert.Equal(SystemFileName, file.GetProperty("name").GetString());
                    CheckHttpSources(file.GetProperty("sources"), reader, source, table);
                }));
            Add($"GET {FileSources}", "cachedRowCount, cachedSchema", reader =>
                ReadDataSetAsync(reader, $"/api/datasets/files/{source.FileId}/sources",
                    body => CheckHttpSources(body, reader, source, table)));
            Add($"GET {SystemCandidates}", "sheetOrPath, rowCount", reader =>
                ReadDataSetAsync(reader, SystemCandidates, body => CheckCandidates(body, reader, source)));
            Add($"GET {FileCandidates}", "sheetOrPath, rowCount", reader =>
                ReadDataSetAsync(reader, $"/api/datasets/files/{source.FileId}/source-candidates",
                    body => CheckCandidates(body, reader, source)));
            Add($"GET {SourcePreview}", $"columns, rows, totalRows и {table.Value}", reader => CheckPreviewAsync(reader, source, table));
            Add($"GET {SourceCsv}", $"заголовок CSV и {table.Value}", reader => CheckCsvAsync(reader, source, table));
            Add("MCP list_datasets", "items[].id, name, sourceCount", async reader =>
            {
                var body = await McpAsync(reader.Client, "list_datasets", new { scope = "System" }, source.FileId.ToString(), false);
                var dataset = Assert.Single(Items(body), item => item.GetProperty("id").GetGuid() == source.FileId);
                Assert.Equal(SystemFileName, dataset.GetProperty("name").GetString());
                Assert.Equal("System", dataset.GetProperty("scope").GetString());
                Assert.Equal(sources.Length, dataset.GetProperty("sourceCount").GetInt32());
            });
            Add("MCP get_dataset", "sources[].id, rawRowCount, columns", async reader => CheckMcpDataset(
                await McpAsync(reader.Client, "get_dataset", new { datasetId = source.FileId }, source.SourceId.ToString(), reader.Count == 2),
                reader, source, table));
            Add("MCP get_source", "rowCount, rawRowCount, columns[].sampleValues", async reader => CheckMcpSource(
                await McpAsync(reader.Client, "get_source", new { sourceId = source.SourceId }, source.SourceId.ToString(), reader.Count == 2),
                reader, source, table));
            Add("MCP resources/read bhs://dataset/{datasetId}", "sources[].id, rawRowCount, columns", async reader => CheckMcpDataset(
                await McpResourceAsync(reader, $"bhs://dataset/{source.FileId}"), reader, source, table));
            Add("MCP resources/read bhs://source/{sourceId}", "rowCount, rawRowCount, columns[].sampleValues", async reader => CheckMcpSource(
                await McpResourceAsync(reader, $"bhs://source/{source.SourceId}"), reader, source, table));
            Add("MCP get_rows", $"totalRows, rows.{table.Value}", async reader =>
            {
                var body = await McpAsync(reader.Client, "get_rows", new { sourceId = source.SourceId }, "totalRows", reader.Count == 2);
                Assert.Equal(reader.Count, body.GetProperty("totalRows").GetInt32());
                var cells = body.GetProperty("rows").EnumerateArray()
                    .Select(row => (row.GetProperty(table.Value).GetString(), row.GetProperty(table.Total).GetString()));
                CheckDataCells(table, reader, cells);
            });

            void Add(string call, string observation, Func<SnapshotReader, Task> verify) =>
                catalog.Add(DataSetProbeName(call, source.Table), observation, () => ForReadersAsync(readers, verify));
        }
    }

    private static async Task<(Guid FileId, Guid SourceId)> PublishDataSetAsync(HttpClient publisher, string table, string key)
    {
        var path = $"{table}/dataset";
        using var published = await publisher.PostAsJsonAsync(path, new { name = key });
        var reference = await ExpectJsonAsync(published, path);
        return (reference.GetProperty("fileId").GetGuid(), reference.GetProperty("sourceId").GetGuid());
    }

    private Task ReadDataSetAsync(SnapshotReader reader, string path, Action<JsonElement> check) =>
        CheckResponseAsync(reader.Client, path, Ok, canReadReport: reader.Count == 2, inspect: check);

    private static void CheckHttpSources(JsonElement entries, SnapshotReader reader, PublishedSource source, TableProbe table)
    {
        var entry = Assert.Single(entries.EnumerateArray(), row => row.GetProperty("id").GetGuid() == source.SourceId);
        Assert.Equal(source.Key, entry.GetProperty("name").GetString());
        Assert.StartsWith($"{source.Key}?columns=", entry.GetProperty("sheetOrPath").GetString(), StringComparison.Ordinal);
        Assert.Equal(reader.Count, entry.GetProperty("cachedRowCount").GetInt32());
        using var schema = JsonDocument.Parse(entry.GetProperty("cachedSchema").GetString()!);
        CheckSourceSamples(schema.RootElement, table, reader);
    }

    private static void CheckCandidates(JsonElement entries, SnapshotReader reader, PublishedSource source)
    {
        var item = Assert.Single(entries.EnumerateArray(), row => row.GetProperty("sheetOrPath").GetString() == source.Key);
        Assert.Equal(reader.Count, item.GetProperty("rowCount").GetInt32());
    }

    private Task CheckPreviewAsync(SnapshotReader reader, PublishedSource source, TableProbe table) =>
        ReadDataSetAsync(reader, $"/api/datasets/sources/{source.SourceId}/preview?maxRows=20", body =>
        {
            Assert.Equal(reader.Count, body.GetProperty("totalRows").GetInt32());
            Assert.Equal(Boundary(table, reader), body.GetProperty("boundary").GetString());
            var columns = body.GetProperty("columns").EnumerateArray().Select(column => column.GetString()).ToArray();
            var text = Array.IndexOf(columns, table.Value);
            var number = Array.IndexOf(columns, table.Total);
            Assert.True(text >= 0 && number >= 0, $"Колонки {table.Value} и {table.Total} отсутствуют.");
            var rows = body.GetProperty("rows").EnumerateArray().Select(row => (row[text].GetString(), row[number].GetString()));
            CheckDataCells(table, reader, rows);
        });

    private async Task CheckCsvAsync(SnapshotReader reader, PublishedSource source, TableProbe table)
    {
        using var response = await reader.Client.GetAsync($"/api/datasets/sources/{source.SourceId}/export?format=csv");
        Assert.Equal(Ok, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        var content = await response.Content.ReadAsStringAsync();
        if (reader.Count != 2) AssertReportNotDisclosed(content, SourceCsv);
        using var csv = new CsvReader(new StringReader(content.TrimStart('\uFEFF')), CultureInfo.InvariantCulture);
        Assert.True(await csv.ReadAsync());
        csv.ReadHeader();
        Assert.Contains(table.Value, csv.HeaderRecord!);
        Assert.Contains(table.Total, csv.HeaderRecord!);
        var actual = new List<(string?, string?)>();
        while (await csv.ReadAsync()) actual.Add((csv.GetField(table.Value), csv.GetField(table.Total)));
        CheckDataCells(table, reader, actual);
    }

    private static void CheckDataCells(TableProbe table, SnapshotReader reader, IEnumerable<(string? Text, string? Number)> cells)
    {
        var expected = ExpectedCells(table, reader.Count).Select(row => (row.Text, row.Number)).OrderBy(row => row.Text);
        var actual = cells.Select(row => (Text: Assert.IsType<string>(row.Text), Number: decimal.Parse(row.Number!, CultureInfo.InvariantCulture)))
            .OrderBy(row => row.Text);
        Assert.Equal(expected, actual);
    }

    private static void CheckSourceSamples(JsonElement columns, TableProbe table, SnapshotReader reader)
    {
        var column = Assert.Single(columns.EnumerateArray(), entry => entry.GetProperty("name").GetString() == table.Value);
        var values = column.GetProperty("sampleValues").EnumerateArray().Select(value => value.GetString()).Order();
        Assert.Equal(ExpectedCells(table, reader.Count).Select(row => row.Text).Order(), values);
    }

    private static void CheckMcpDataset(JsonElement body, SnapshotReader reader, PublishedSource source, TableProbe table)
    {
        Assert.Equal(source.FileId, body.GetProperty("id").GetGuid());
        var entry = Assert.Single(body.GetProperty("sources").EnumerateArray(), item => item.GetProperty("id").GetGuid() == source.SourceId);
        Assert.Equal(reader.Count, entry.GetProperty("rawRowCount").GetInt32());
        Assert.Contains(entry.GetProperty("columns").EnumerateArray(), column => column.GetString() == table.Value);
        Assert.Contains(entry.GetProperty("columns").EnumerateArray(), column => column.GetString() == table.Total);
    }

    private static void CheckMcpSource(JsonElement body, SnapshotReader reader, PublishedSource source, TableProbe table)
    {
        Assert.Equal(source.SourceId, body.GetProperty("id").GetGuid());
        Assert.Equal(source.FileId, body.GetProperty("datasetId").GetGuid());
        Assert.Equal(reader.Count, body.GetProperty("rowCount").GetInt32());
        Assert.Equal(reader.Count, body.GetProperty("rawRowCount").GetInt32());
        CheckSourceSamples(body.GetProperty("columns"), table, reader);
    }

    private async Task<JsonElement> McpResourceAsync(SnapshotReader reader, string uri)
    {
        var result = await McpTestClient.CallAsync(reader.Client, "resources/read", new { uri });
        var entry = Assert.Single(result.GetProperty("contents").EnumerateArray());
        Assert.Equal(uri, entry.GetProperty("uri").GetString());
        return ParseMcpText(entry, uri, reader.Count == 2);
    }

    private async Task<JsonElement> McpAsync(HttpClient client, string name, object arguments, string signal, bool canReadReport)
    {
        var result = await McpTestClient.CallAsync(client, "tools/call", new { name, arguments });
        var entry = Assert.Single(result.GetProperty("content").EnumerateArray());
        Assert.Contains(signal, entry.GetProperty("text").GetString()!, StringComparison.Ordinal);
        return ParseMcpText(entry, name, canReadReport);
    }

    private JsonElement ParseMcpText(JsonElement entry, string name, bool canReadReport)
    {
        var content = entry.GetProperty("text").GetString()!;
        if (!canReadReport) AssertReportNotDisclosed(content, name);
        using var document = JsonDocument.Parse(content);
        return document.RootElement.Clone();
    }
}
