using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.DataSets;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Shared pipeline step used by both generation (DataSetResolver) and preview (DataSetService):
/// download blob → parse → apply computed columns → apply row filter.
/// The final column→field mapping differs per caller and stays in the caller.
/// </summary>
public static class DataSetBindingProcessor
{
    public static async Task<List<IReadOnlyDictionary<string, string?>>> LoadRowsAsync(
        IBlobStorage blob,
        DataSetParserFactory parserFactory,
        string blobPath,
        DataSetFormat format,
        string sheetOrPath,
        string? columnExpressions,
        string? computedColumns,
        string? rowFilter,
        CancellationToken ct)
    {
        await using var stream = await blob.DownloadAsync(blobPath, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        var parser = parserFactory.GetParser(format);
        var parsed = await parser.ParseAsync(bytes, sheetOrPath, columnExpressions, ct);

        // Computed columns first (they may be referenced by the filter), then filter.
        var rows = DataSetComputedColumnExecutor.Apply(computedColumns, parsed.Rows.ToList());
        rows = DataSetRowFilterExecutor.Apply(rowFilter, rows);
        return rows;
    }
}
