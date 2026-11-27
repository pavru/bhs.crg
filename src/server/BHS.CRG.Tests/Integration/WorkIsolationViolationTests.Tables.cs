using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Tests.Work;
using NPOI.XSSF.UserModel;
using Xunit.Sdk;

namespace BHS.CRG.Tests.Integration;

internal sealed partial class WorkIsolationScenario
{
    private const string ForeignReportKey = "work.reports: в keys найден ключ отчёта Б у монтажника А.";

    private sealed record SnapshotReader(HttpClient Client, int Count, string Boundary);

    private sealed record TableProbe(string Path, string Value, string Label, string Total, Guid KeyA, Guid KeyB,
        string CellA, string CellB, decimal ValueA, decimal ValueB, string AllBoundary);

    private TableProbe[] Tables() =>
    [
        new(WorkTableWorld.Reports, "Сотрудник", "Сотрудник", "Часы", reportA, reportB, "Монтажник А", "Монтажник Б",
            HoursA, HoursB, "Все отчёты. Черновики других сотрудников видны без содержимого до подачи"),
        new(WorkTableWorld.Lines, "Место", "Место работ", "Объём", lineA, lineB, "Участок А", marker,
            VolumeA, VolumeB, "Все отчёты. Строки чужих черновиков появляются после подачи"),
    ];

    private static SnapshotReader[] SnapshotReaders(HttpClient installer, HttpClient unassigned, HttpClient executive,
        bool expanded) =>
        [new(installer, expanded ? 2 : 1, expanded ? "all" : "own"),
            new(unassigned, expanded ? 2 : 0, expanded ? "all" : "not-assigned"), new(executive, 2, "all")];

    private static IEnumerable<string> TableProbeNames()
    {
        yield return "GET /api/tables";
        foreach (var path in new[] { WorkTableWorld.Reports, WorkTableWorld.Lines })
        {
            yield return $"GET {path}?totals";
            yield return $"GET {path}?row";
            yield return $"POST {path}/query";
            yield return $"POST {path}/export";
        }
    }

    private async Task CheckTablesAsync(HttpClient installer, HttpClient unassigned, HttpClient executive)
    {
        var catalog = new ProbeCatalog();
        RegisterTableProbes(catalog, SnapshotReaders(installer, unassigned, executive, expanded: false));
        await catalog.RunAsync(TableProbeNames());
    }

    private void RegisterTableProbes(ProbeCatalog catalog, SnapshotReader[] readers)
    {
        var tables = Tables();
        catalog.Add("GET /api/tables", "address", () => ForReadersAsync(readers, reader =>
            CheckResponseAsync(reader.Client, "/api/tables", Ok, inspect: body =>
            {
                var addresses = body.EnumerateArray().Select(item => item.GetProperty("address").GetString()).ToArray();
                Assert.Contains("work.reports", addresses);
                Assert.Contains("work.lines", addresses);
            })));
        foreach (var table in tables)
        {
            RegisterTablePage(catalog, table, readers);
            catalog.Add($"GET {table.Path}?row", $"keys, rows.{table.Value}",
                () => ForReadersAsync(readers, reader => CheckTableRowAsync(table, reader)));
            catalog.Add($"POST {table.Path}/query", $"keys, rows.{table.Value}, totals.{table.Total}",
                () => ForReadersAsync(readers, reader => CheckTableQueryAsync(table, reader)));
            catalog.Add($"POST {table.Path}/export", $"XLSX, колонки {table.Label} и {table.Total}",
                () => ForReadersAsync(readers, reader => CheckTableExportAsync(table, reader)));
        }
    }

    private void RegisterTablePage(ProbeCatalog catalog, TableProbe table, SnapshotReader[] readers) =>
        catalog.Add($"GET {table.Path}?totals", $"keys, rows.{table.Value}, totals.{table.Total}",
            () => ForReadersAsync(readers, reader => CheckTablePageAsync(table, reader)));

    private async Task CheckProbeFailuresAsync(HttpClient installer)
    {
        var table = Tables().Single(table => table.Path == WorkTableWorld.Reports);
        var name = $"GET {table.Path}?totals";
        var catalog = new ProbeCatalog();
        RegisterTablePage(catalog, table, [new(installer, 2, "all")]);

        var skipped = await Assert.ThrowsAsync<TrueException>(() => catalog.RunAsync([name], skip: name));
        Assert.Contains(name, skipped.Message, StringComparison.Ordinal);

        // До включения контроля под А отвечает настоящий узкий хост. Проба ожидает обязательный ключ Б.
        var silent = await Assert.ThrowsAsync<XunitException>(() => catalog.RunAsync([name]));
        Assert.Contains(name, silent.Message, StringComparison.Ordinal);
        Assert.Contains("keys: нет обязательного ключа Б", silent.Message, StringComparison.Ordinal);
    }

    private static async Task ForReadersAsync(SnapshotReader[] readers, Func<SnapshotReader, Task> read)
    {
        foreach (var reader in readers) await read(reader);
    }

    private async Task AssertReportBExcludedAsync(HttpClient client)
    {
        using var response = await client.GetAsync(WorkTableWorld.Reports);
        var body = await ExpectJsonAsync(response, WorkTableWorld.Reports);
        var keys = body.GetProperty("keys").EnumerateArray().Select(value => value.GetGuid()).ToArray();
        Assert.False(keys.Contains(reportB), ForeignReportKey);
        Assert.Equal(reportA, Assert.Single(keys));
    }

    private async Task CheckTablePageAsync(TableProbe table, SnapshotReader reader)
    {
        var path = $"{table.Path}?totals={Uri.EscapeDataString(table.Total)}";
        using var response = await reader.Client.GetAsync(path);
        var body = await ExpectJsonAsync(response, path);
        if (reader.Count == 2)
            Assert.True(body.GetProperty("keys").EnumerateArray().Any(key => key.GetGuid() == table.KeyB),
                $"{table.Path}, keys: нет обязательного ключа Б.");
        AssertTableRows(body, table, reader, ExpectedCells(table, reader.Count));
        CheckNumericTotal(body, path, table.Total, ExpectedCells(table, reader.Count).Select(row => row.Number).ToArray());
    }

    private async Task CheckTableRowAsync(TableProbe table, SnapshotReader reader)
    {
        var path = $"{table.Path}?row={table.KeyB}";
        if (reader.Count != 2)
        {
            await CheckResponseAsync(reader.Client, path, Missing);
            return;
        }
        var body = await CheckTableAsync(reader.Client, path, 1, reader.Boundary);
        AssertTableRows(body, table, reader, [(table.KeyB, table.CellB, table.ValueB)]);
    }

    private async Task CheckTableQueryAsync(TableProbe table, SnapshotReader reader)
    {
        var path = $"{table.Path}/query";
        var filter = JsonSerializer.Serialize(new { type = "condition", column = table.Value, op = "contains", value = table.CellB });
        using var response = await reader.Client.PostAsJsonAsync(path, new { filter, totals = new[] { table.Total } });
        var body = await ExpectJsonAsync(response, path);
        var expected = reader.Count == 2 ? ExpectedCells(table, 2).Skip(1).ToArray() : [];
        AssertTableRows(body, table, reader, expected);
        CheckNumericTotal(body, path, table.Total, expected.Select(row => row.Number).ToArray());
    }

    private async Task CheckTableExportAsync(TableProbe table, SnapshotReader reader)
    {
        var path = $"{table.Path}/export";
        using var response = await reader.Client.PostAsJsonAsync(path, new { columns = new[] { table.Value, table.Total } });
        Assert.Equal(Ok, response.StatusCode);
        using var workbook = new XSSFWorkbook(await response.Content.ReadAsStreamAsync());
        var sheet = workbook.GetSheetAt(0);
        var rows = Enumerable.Range(0, sheet.LastRowNum + 1).Select(sheet.GetRow).Where(row => row is not null).ToArray();
        var heading = Assert.Single(rows, row => row.GetCell(0)?.ToString() == table.Label);
        Assert.Equal(table.Total, heading.GetCell(1).StringCellValue);
        Assert.Contains(rows, row => row.GetCell(0)?.ToString() == Boundary(table, reader));
        Assert.Contains(rows, row => row.GetCell(0)?.ToString() == $"Строк: {reader.Count}");
        var actual = rows.Where(row => row.RowNum > heading.RowNum)
            .Select(row => (row.GetCell(0).StringCellValue, (decimal)row.GetCell(1).NumericCellValue)).OrderBy(row => row.Item1);
        var expected = ExpectedCells(table, reader.Count).Select(row => (row.Text, row.Number)).OrderBy(row => row.Text);
        Assert.Equal(expected, actual);
    }

    private void AssertTableRows(JsonElement body, TableProbe table, SnapshotReader reader,
        (Guid Key, string Text, decimal Number)[] expected)
    {
        Assert.Equal(reader.Boundary, body.GetProperty("boundaryOutcome").GetString());
        Assert.Equal(Boundary(table, reader), body.GetProperty("boundary").GetString());
        Assert.Equal(reader.Count == 0 ? "not-assigned" : null, body.GetProperty("reason").GetString());
        Assert.Equal(expected.Length, body.GetProperty("count").GetInt32());
        var keys = body.GetProperty("keys").EnumerateArray().Select(value => value.GetGuid());
        var rows = body.GetProperty("rows").EnumerateArray();
        Assert.Equal(expected.Length, body.GetProperty("rows").GetArrayLength());
        var cells = keys.Zip(rows, (key, row) => (key, row.GetProperty(table.Value).GetString(), row.GetProperty(table.Total).GetDecimal()));
        Assert.Equal(expected.OrderBy(row => row.Key), cells.OrderBy(row => row.key));
        if (reader.Count != 2) AssertReportNotDisclosed(body.GetRawText(), table.Path);
    }

    private static string Boundary(TableProbe table, SnapshotReader reader) => reader.Boundary switch
    {
        "all" => table.AllBoundary,
        "own" => "Ваши отчёты",
        _ => "Отчёты ваших строек (0). Вы не назначены ни на одну стройку",
    };

    private static (Guid Key, string Text, decimal Number)[] ExpectedCells(TableProbe table, int count) => count switch
    {
        0 => [],
        1 => [(table.KeyA, table.CellA, table.ValueA)],
        _ => [(table.KeyA, table.CellA, table.ValueA), (table.KeyB, table.CellB, table.ValueB)],
    };
}
