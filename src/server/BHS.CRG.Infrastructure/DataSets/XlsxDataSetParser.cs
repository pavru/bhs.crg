using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.DataSets;
using ExcelDataReader;
using System.Data;

namespace BHS.CRG.Infrastructure.DataSets;

public class XlsxDataSetParser : IDataSetParser
{
    public bool CanParse(DataSetFormat format) => format is DataSetFormat.Xlsx or DataSetFormat.Xls;

    public Task<IReadOnlyList<DataSetSourceInfo>> DetectSourcesAsync(byte[] bytes, CancellationToken ct)
    {
        var ds = ReadExcel(bytes);
        var sources = ds.Tables.Cast<DataTable>().Select(table =>
        {
            var columns = ExtractColumns(table);
            return new DataSetSourceInfo(table.TableName, table.TableName, columns, table.Rows.Count);
        }).ToList();
        return Task.FromResult<IReadOnlyList<DataSetSourceInfo>>(sources);
    }

    public Task<DataSetParseResult> ParseAsync(byte[] bytes, string sheetOrPath, string? columnExpressions, CancellationToken ct)
    {
        var ds = ReadExcel(bytes);
        var table = ds.Tables[sheetOrPath] ?? ds.Tables[0];
        if (table == null) return Task.FromResult(new DataSetParseResult([], []));

        var columns = ExtractColumns(table);
        var rows = new List<IReadOnlyDictionary<string, string?>>();
        foreach (DataRow row in table.Rows)
        {
            var dict = new Dictionary<string, string?>();
            foreach (DataColumn col in table.Columns)
                dict[col.ColumnName] = row[col]?.ToString()?.Trim();
            rows.Add(dict);
        }

        return Task.FromResult(new DataSetParseResult(columns, rows));
    }

    private static DataSet ReadExcel(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        return reader.AsDataSet(new ExcelDataSetConfiguration
        {
            ConfigureDataTable = _ => new ExcelDataTableConfiguration
            {
                UseHeaderRow = true,
                FilterRow = rowReader => rowReader.Depth > 0, // skip completely empty rows
            },
        });
    }

    private static DataSetColumnInfo[] ExtractColumns(DataTable table)
    {
        const int sampleCount = 3;
        return table.Columns.Cast<DataColumn>().Select(col =>
        {
            var samples = table.Rows.Cast<DataRow>()
                .Take(sampleCount)
                .Select(r => r[col]?.ToString() ?? "")
                .ToArray();
            return new DataSetColumnInfo(col.ColumnName, samples);
        }).ToArray();
    }
}
