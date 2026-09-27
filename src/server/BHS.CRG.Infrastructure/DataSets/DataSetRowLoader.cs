using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
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
/// перезапускает распознавание — см. DataSetService.RecognizePdfSourceAsync. Системные наборы —
/// третий случай: строки консолидирует провайдер из данных самой системы, живьём (запрос к БД
/// дешевле скачивания блоба, а кэш означал бы реестр, отставший от состава комплекта).
/// </summary>
public class DataSetRowLoader(
    IBlobStorage blob,
    DataSetParserFactory parserFactory,
    SystemDataProviderRegistry systemProviders) : IDataSetRowLoader
{
    public async Task<List<IReadOnlyDictionary<string, string?>>> LoadRowsAsync(
        DataSetSource source,
        DataAccess access,
        CancellationToken ct)
        => [.. (await LoadAsync(source, access, ct)).Rows];

    public async Task<LoadedRows> LoadAsync(
        DataSetSource source,
        DataAccess access,
        CancellationToken ct)
    {
        List<IReadOnlyDictionary<string, string?>> parsedRows;
        // Колонки и оговорка известны ровно здесь, вместе со строками (issue #661/#664): извлечение
        // — единственное место, где видно, что источник отдал НА САМОМ ДЕЛЕ, а не что о нём было
        // записано при создании. Спрашивать то же второй раз значило бы повторить всю загрузку.
        IReadOnlyList<DataSetColumnInfo>? columns = null;
        string? warning = null;
        string? boundary = null;
        if (source.File.Format == DataSetFormat.Pdf)
        {
            // Кэш распознавания — сам себе описание: CachedSchema писался тем же проходом, что и
            // CachedData, и разойтись им не на чем.
            parsedRows = DeserializeCachedData(source.CachedData);
        }
        else if (source.File.Format == DataSetFormat.System)
        {
            // Ворота стоят ЗДЕСЬ, в единственной точке извлечения строк, а не у каждого из пяти
            // путей чтения (ТЗ CORE-24.1, issue #965). Пять проверок разошлись бы при первой правке,
            // и разошлись бы молча: путь, забывший спросить, выглядит работающим.
            var provider = systemProviders.Get(source.SheetOrPath);
            SystemDataSetGate.Ensure(provider.Declaration, access, source.Name);
            var provided = await provider
                .ProvideAsync(source.SheetOrPath, source.File.Scope, source.File.ScopeId, access, ct);
            parsedRows = provided.Rows.ToList();
            columns = provided.Columns;
            warning = provided.Warning;
            // Подпись к данным — ПОСТОЯННАЯ, а не сообщение об ошибке (ТЗ CORE-24.3): человек должен
            // видеть, что именно ему отдали, и в удачном случае тоже. «12 строк скрыто» не годится:
            // две разные цифры, обе выглядящие окончательными, опаснее одной с оговоркой.
            boundary = provider.Declaration.BoundaryFor(provided.Boundary);
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
            columns = parsed.Columns;
            warning = parsed.Warning;
        }

        // Transformation (вычисляемые колонки могут понадобиться фильтру/сортировке), затем Filter, затем Sort.
        var rows = DataSetComputedColumnExecutor.Apply(source.ComputedColumns, parsedRows);
        rows = DataSetRowFilterExecutor.Apply(source.RowFilter, rows);
        rows = DataSetSortExecutor.Apply(source.SortSpec, rows);
        return new LoadedRows(rows, parsedRows.Count, columns, warning, boundary);
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
