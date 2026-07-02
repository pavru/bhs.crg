using BHS.CRG.Application.Common;
using BHS.CRG.Domain.DataSets;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Shared pipeline step used by both generation (DataSetResolver) and preview (DataSetService):
/// download blob → parse (extraction) → computed columns (transformation) → row filter → sort.
/// Filter/Transformation/Sort — с DataSetSource, либо со связанного DataSetProcessingTemplate,
/// если задан ProcessingTemplateId (см. ResolveProcessing). Требует source.File и
/// source.ProcessingTemplate загруженными (.Include) заранее у вызывающего кода.
/// The final column→field mapping differs per caller and stays in the caller.
/// </summary>
public static class DataSetBindingProcessor
{
    /// <summary>Эффективные Filter/Transformation/Sort: из шаблона (если связан), иначе свои.</summary>
    public static (string? RowFilter, string? ComputedColumns, string? SortSpec) ResolveProcessing(DataSetSource source)
        => source.ProcessingTemplateId is not null && source.ProcessingTemplate is not null
            ? (source.ProcessingTemplate.RowFilter, source.ProcessingTemplate.ComputedColumns, source.ProcessingTemplate.SortSpec)
            : (source.RowFilter, source.ComputedColumns, source.SortSpec);

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

        var (rowFilter, computedColumns, sortSpec) = ResolveProcessing(source);

        // Transformation (вычисляемые колонки могут понадобиться фильтру/сортировке), затем Filter, затем Sort.
        var rows = DataSetComputedColumnExecutor.Apply(computedColumns, parsed.Rows.ToList());
        rows = DataSetRowFilterExecutor.Apply(rowFilter, rows);
        rows = DataSetSortExecutor.Apply(sortSpec, rows);
        return rows;
    }
}
