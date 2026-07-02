using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.DataSets;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Shared pipeline step used by both generation (DataSetResolver) and preview (DataSetService):
/// extraction → computed columns (transformation) → row filter → sort. Filter/Transformation/Sort
/// — свои на DataSetSource (применение шаблона обработки копирует его значения сюда единожды, не
/// живая ссылка — см. DataSetProcessingTemplate). Требует source.File загруженным (.Include)
/// заранее у вызывающего кода. The final column→field mapping differs per caller and stays there.
///
/// Extraction для большинства форматов — перепарсинг blob при каждом вызове (дёшево,
/// детерминированно). PDF — исключение: Extraction через vision-LLM (дорого/недетерминированно),
/// поэтому читает уже распознанные и закэшированные строки (DataSetSource.CachedData), не
/// перезапускает распознавание — см. DataSetService.RecognizePdfSourceAsync.
/// </summary>
public static class DataSetBindingProcessor
{
    public static async Task<List<IReadOnlyDictionary<string, string?>>> LoadRowsAsync(
        IBlobStorage blob,
        DataSetParserFactory parserFactory,
        DataSetSource source,
        CancellationToken ct)
    {
        List<IReadOnlyDictionary<string, string?>> parsedRows;
        if (source.File.Format == DataSetFormat.Pdf)
        {
            parsedRows = DeserializeCachedData(source.CachedData);
        }
        else
        {
            await using var stream = await blob.DownloadAsync(source.File.BlobPath, ct);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();

            var parser = parserFactory.GetParser(source.File.Format);
            var parsed = await parser.ParseAsync(bytes, source.SheetOrPath, source.ColumnExpressions, ct);
            parsedRows = parsed.Rows.ToList();
        }

        // Transformation (вычисляемые колонки могут понадобиться фильтру/сортировке), затем Filter, затем Sort.
        var rows = DataSetComputedColumnExecutor.Apply(source.ComputedColumns, parsedRows);
        rows = DataSetRowFilterExecutor.Apply(source.RowFilter, rows);
        rows = DataSetSortExecutor.Apply(source.SortSpec, rows);
        return rows;
    }

    private static List<IReadOnlyDictionary<string, string?>> DeserializeCachedData(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var rows = JsonSerializer.Deserialize<List<Dictionary<string, string?>>>(json);
            return rows?.Select(r => (IReadOnlyDictionary<string, string?>)r).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
