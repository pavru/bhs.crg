using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.DataSets;
using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;

namespace BHS.CRG.Infrastructure.DataSets;

public class CsvDataSetParser : IDataSetParser
{
    public bool CanParse(DataSetFormat format) => format is DataSetFormat.Csv;

    public Task<IReadOnlyList<DataSetSourceInfo>> DetectSourcesAsync(byte[] bytes, CancellationToken ct)
    {
        var result = ParseInternal(bytes);
        var info = new DataSetSourceInfo("default", "default", result.Columns, result.Rows.Count);
        return Task.FromResult<IReadOnlyList<DataSetSourceInfo>>([info]);
    }

    public Task<DataSetParseResult> ParseAsync(byte[] bytes, string sheetOrPath, CancellationToken ct)
        => Task.FromResult(ParseInternal(bytes));

    private static DataSetParseResult ParseInternal(byte[] bytes)
    {
        // Detect delimiter: try tab first, then comma
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        var firstLine = text.Split('\n').FirstOrDefault() ?? "";
        var delimiter = firstLine.Contains('\t') ? "\t" : ",";

        using var reader = new StreamReader(new MemoryStream(bytes));
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            Delimiter = delimiter,
            MissingFieldFound = null,
            BadDataFound = null,
            TrimOptions = TrimOptions.Trim,
        });

        if (!csv.Read() || !csv.ReadHeader()) return new DataSetParseResult([], []);
        var headers = csv.HeaderRecord ?? [];

        var rows = new List<IReadOnlyDictionary<string, string?>>();
        while (csv.Read())
        {
            var row = new Dictionary<string, string?>();
            foreach (var h in headers)
                row[h] = csv.GetField(h)?.Trim();
            rows.Add(row);
        }

        const int sampleCount = 3;
        var columns = headers.Select(h => new DataSetColumnInfo(
            h,
            rows.Take(sampleCount).Select(r => r.TryGetValue(h, out var v) ? v ?? "" : "").ToArray()
        )).ToArray();

        return new DataSetParseResult(columns, rows);
    }
}
