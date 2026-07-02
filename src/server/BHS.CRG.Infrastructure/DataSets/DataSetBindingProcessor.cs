using BHS.CRG.Application.Common;
using BHS.CRG.Domain.DataSets;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Shared pipeline step used by both generation (DataSetResolver) and preview (DataSetService):
/// download blob → parse (extraction) → computed columns (transformation) → row filter → sort.
/// Filter/Transformation/Sort — свои на DataSetSource (применение шаблона обработки копирует
/// его значения сюда единожды, не живая ссылка — см. DataSetProcessingTemplate). Требует
/// source.File загруженным (.Include) заранее у вызывающего кода.
/// The final column→field mapping differs per caller and stays in the caller.
/// </summary>
public static class DataSetBindingProcessor
{
    public static async Task<List<IReadOnlyDictionary<string, string?>>> LoadRowsAsync(
        IBlobStorage blob,
        DataSetParserFactory parserFactory,
        DataSetSource source,
        CancellationToken ct)
    {
        await using var stream = await blob.DownloadAsync(source.File.BlobPath, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        var parser = parserFactory.GetParser(source.File.Format);
        var parsed = await parser.ParseAsync(bytes, source.SheetOrPath, source.ColumnExpressions, ct);

        // Transformation (вычисляемые колонки могут понадобиться фильтру/сортировке), затем Filter, затем Sort.
        var rows = DataSetComputedColumnExecutor.Apply(source.ComputedColumns, parsed.Rows.ToList());
        rows = DataSetRowFilterExecutor.Apply(source.RowFilter, rows);
        rows = DataSetSortExecutor.Apply(source.SortSpec, rows);
        return rows;
    }
}
