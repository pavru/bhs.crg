using System.IO.Compression;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Источники набора данных (обычные, не PDF-распознавание): CRUD/preview/export/автомаппинг/duplicate,
/// zip-entries, предпросмотр выражений, назначение обработки/применение шаблона обработки.
/// Часть декомпозиции <see cref="DataSetService"/> (см. архитектурный отчёт, «Предложение 3»).
/// </summary>
public class DataSetSourceService(
    AppDbContext db,
    IBlobStorage blob,
    DataSetParserFactory parserFactory)
{
    private record CachedColumnInfo(string Name, string[] SampleValues);

    // cachedSchema stores camelCase keys ("name"/"sampleValues") — match them case-insensitively.
    private static readonly JsonSerializerOptions CachedSchemaJson = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<DataSetSourceDto>> ListSourcesAsync(Guid fileId, CancellationToken ct)
    {
        var sources = await db.DataSetSources.Where(s => s.FileId == fileId).AsNoTracking().ToListAsync(ct);
        return sources.Select(DataSetDtoMapper.MapSource).ToList();
    }

    /// <summary>
    /// Детект «кандидатов» на источник в сыром файле (листы XLSX, top-level массивы JSON, «весь файл»
    /// для CSV) — БЕЗ персиста. Используется диалогом создания источника как подсказки в один клик.
    /// Для XML парсер кандидатов не даёт (пусто) — источник строится вручную через XPath-builder.
    /// </summary>
    public async Task<IReadOnlyList<DataSetSourceInfo>> DetectSourceCandidatesAsync(Guid fileId, CancellationToken ct)
    {
        var file = await db.DataSetFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId, ct)
            ?? throw new KeyNotFoundException($"DataSetFile {fileId} not found");

        await using var stream = await blob.DownloadAsync(file.BlobPath, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);

        var parser = parserFactory.GetParser(file.Format);
        return await parser.DetectSourcesAsync(ms.ToArray(), ct);
    }

    public async Task<SourcePreviewDto?> PreviewSourceAsync(Guid sourceId, int maxRows, CancellationToken ct)
    {
        var source = await db.DataSetSources.Include(s => s.File).AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;

        var rows = await DataSetBindingProcessor.LoadRowsAsync(blob, parserFactory, source, ct);

        var take = maxRows <= 0 ? 50 : maxRows;
        // Базовые колонки — из уже сохранённого кэша схемы (тот же парсер заполнил его при
        // создании/обновлении источника), не повторный парсинг: для PDF вообще нет "живого"
        // парсинга (см. LoadRowsAsync), а для остальных форматов результат эквивалентен.
        var baseColumns = JsonSerializer.Deserialize<CachedColumnInfo[]>(source.CachedSchema, CachedSchemaJson) ?? [];
        var columns = baseColumns.Select(c => c.Name).ToList();
        // Вычисляемые колонки могут добавить новые имена, которых нет в исходном разборе.
        columns.AddRange(rows.SelectMany(r => r.Keys).Distinct().Except(columns));

        var previewRows = rows.Take(take)
            .Select(r => (IReadOnlyList<string?>)columns.Select(c => r.TryGetValue(c, out var v) ? v : null).ToList())
            .ToList();
        return new SourcePreviewDto(columns, previewRows, rows.Count);
    }

    public async Task<SourceExportDto?> ExportSourceAsync(Guid sourceId, string? format, CancellationToken ct)
    {
        var source = await db.DataSetSources.Include(s => s.File).AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;

        // Все строки после обработки (Filter/Transformation/Sort) — тот же путь, что и превью, без лимита.
        var rows = await DataSetBindingProcessor.LoadRowsAsync(blob, parserFactory, source, ct);
        var baseColumns = JsonSerializer.Deserialize<CachedColumnInfo[]>(source.CachedSchema, CachedSchemaJson) ?? [];
        var columns = baseColumns.Select(c => c.Name).ToList();
        columns.AddRange(rows.SelectMany(r => r.Keys).Distinct().Except(columns));

        var exportRows = rows
            .Select(r => (IReadOnlyList<string?>)columns.Select(c => r.TryGetValue(c, out var v) ? v : null).ToList())
            .ToList();

        var (bytes, ext, contentType) = SpreadsheetExporter.Export(
            SpreadsheetExporter.ParseFormat(format), columns, exportRows, sheetName: source.Name);
        var fileName = $"{DataSetDtoMapper.SanitizeFileName(source.Name)}.{ext}";
        return new SourceExportDto(bytes, fileName, contentType);
    }

    public async Task<Dictionary<string, string>?> AutoMapAsync(
        Guid sourceId, IReadOnlyList<FieldInfo> fields, CancellationToken ct)
    {
        var source = await db.DataSetSources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;

        var columns = JsonSerializer.Deserialize<CachedColumnInfo[]>(source.CachedSchema, CachedSchemaJson) ?? [];
        return DataSetAutoMapper.AutoMap(columns.Select(c => c.Name).ToList(), fields);
    }

    public async Task<DataSetSourceDto> CreateSourceAsync(Guid fileId, CreateSourceInput input, CancellationToken ct)
    {
        var file = await db.DataSetFiles.Include(f => f.Sources).FirstOrDefaultAsync(f => f.Id == fileId, ct)
            ?? throw new KeyNotFoundException($"DataSetFile {fileId} not found");

        var columnExpressionsJson = DataSetDtoMapper.SerializeColumnExpressions(input.ColumnExpressions);
        var (schema, rowCount) = await ParseForDefinitionAsync(file.BlobPath, file.Format, input.SheetOrPath, columnExpressionsJson, ct);

        var source = file.AddSource(input.Name.Trim(), input.SheetOrPath.Trim(), DataSetDtoMapper.SerializeSchema(schema), rowCount, columnExpressionsJson);
        // file уже отслеживается (загружен из БД) — новый дочерний источник, добавленный в его
        // коллекцию навигации, EF не распознаёт как Added автоматически (Guid — клиентский ключ,
        // не default-значение), поэтому без явного Add() трекер помечает его Modified и
        // пытается сделать UPDATE несуществующей строки → DbUpdateConcurrencyException.
        db.DataSetSources.Add(source);
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapSource(source);
    }

    public async Task<DataSetSourceDto?> UpdateSourceAsync(Guid sourceId, UpdateSourceInput input, CancellationToken ct)
    {
        var source = await db.DataSetSources.Include(s => s.File).FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;

        var columnExpressionsJson = DataSetDtoMapper.SerializeColumnExpressions(input.ColumnExpressions);
        var (schema, rowCount) = await ParseForDefinitionAsync(
            source.File.BlobPath, source.File.Format, input.SheetOrPath, columnExpressionsJson, ct);

        source.UpdateDefinition(input.Name.Trim(), input.SheetOrPath.Trim(), columnExpressionsJson);
        source.UpdateCache(DataSetDtoMapper.SerializeSchema(schema), rowCount);
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapSource(source);
    }

    public async Task<bool> DeleteSourceAsync(Guid sourceId, CancellationToken ct)
    {
        var source = await db.DataSetSources.FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return false;

        var bindings = await db.DataSetBindings.Where(b => b.SourceId == sourceId).ToListAsync(ct);
        if (bindings.Count > 0)
        {
            var usages = await DescribeBindingUsagesAsync(bindings, ct);
            throw new InvalidOperationException(
                $"Источник используется в привязках: {string.Join("; ", usages)} — сначала удалите привязки.");
        }

        db.DataSetSources.Remove(source);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // Человекочитаемое описание, где именно используется источник (для сообщения об ошибке
    // удаления) — по DocumentInstance (документ + комплект) и по CommonDataEntry (запись каталога).
    private async Task<List<string>> DescribeBindingUsagesAsync(List<DataSetBinding> bindings, CancellationToken ct)
    {
        var usages = new List<string>();

        var instanceIds = bindings.Where(b => b.InstanceId is not null).Select(b => b.InstanceId!.Value).Distinct().ToList();
        if (instanceIds.Count > 0)
        {
            var instances = await db.DocumentInstances
                .Where(i => instanceIds.Contains(i.Id))
                .Select(i => new { i.Id, i.Name, i.DocumentTypeId, i.DocumentSetId })
                .ToListAsync(ct);
            var typeIds = instances.Select(i => i.DocumentTypeId).Distinct().ToList();
            var setIds = instances.Select(i => i.DocumentSetId).Distinct().ToList();
            var typeNames = await db.DocumentTypes.Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
            var setNames = await db.DocumentSets.Where(s => setIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
            foreach (var inst in instances)
            {
                var label = inst.Name ?? typeNames.GetValueOrDefault(inst.DocumentTypeId, "документ");
                var setName = setNames.GetValueOrDefault(inst.DocumentSetId);
                usages.Add(setName is not null ? $"документ «{label}» (комплект «{setName}»)" : $"документ «{label}»");
            }
        }

        var entryIds = bindings.Where(b => b.CommonDataEntryId is not null).Select(b => b.CommonDataEntryId!.Value).Distinct().ToList();
        if (entryIds.Count > 0)
        {
            var entryNames = await db.CommonDataEntries.Where(e => entryIds.Contains(e.Id)).Select(e => e.DisplayName).ToListAsync(ct);
            usages.AddRange(entryNames.Select(name => $"запись каталога «{name}»"));
        }

        return usages;
    }

    // Копия источника на том же файле — тот же locator/колонки/обработка (Filter/Transformation/Sort),
    // но независимая: правки одной копии не затрагивают другую. Позволяет получить несколько
    // наборов на основе одного файла без переопределения extraction с нуля (актуально и для
    // форматов без ручного builder'а — CSV/XLSX — где нужно только разное Filter/Transformation/Sort
    // поверх одинаковых данных).
    public async Task<DataSetSourceDto?> DuplicateSourceAsync(Guid sourceId, CancellationToken ct)
    {
        var source = await db.DataSetSources.Include(s => s.File).FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;

        var copy = source.File.AddSource(
            $"{source.Name} (копия)", source.SheetOrPath, source.CachedSchema, source.CachedRowCount,
            source.ColumnExpressions, source.CachedData);
        copy.SetProcessing(source.RowFilter, source.ComputedColumns, source.SortSpec);
        copy.SetTags(source.Tags);
        // file уже отслеживается — см. пояснение в CreateSourceAsync (иначе Modified вместо Added).
        db.DataSetSources.Add(copy);
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapSource(copy);
    }

    // Скачивает файл и парсит указанное определение — используется для валидации и первичного
    // расчёта кэша при ручном создании/редактировании источника (в первую очередь для XML).
    private async Task<(IReadOnlyList<DataSetColumnInfo> Schema, int RowCount)> ParseForDefinitionAsync(
        string blobPath, DataSetFormat format, string sheetOrPath, string? columnExpressionsJson, CancellationToken ct)
    {
        await using var stream = await blob.DownloadAsync(blobPath, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);

        var parser = parserFactory.GetParser(format);
        try
        {
            var result = await parser.ParseAsync(ms.ToArray(), sheetOrPath, columnExpressionsJson, ct);
            return (result.Columns, result.Rows.Count);
        }
        catch (Exception ex) when (ex is System.Xml.XPath.XPathException or ArgumentException
            or System.Xml.XmlException or InvalidOperationException or JsonCons.JsonPath.JsonPathParseException)
        {
            throw new ArgumentException($"Не удалось разобрать выражение: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<string>> ListZipXmlEntriesAsync(Guid fileId, CancellationToken ct)
    {
        var file = await db.DataSetFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId, ct)
            ?? throw new KeyNotFoundException($"DataSetFile {fileId} not found");
        if (file.Format != DataSetFormat.Zip) return [];

        await using var stream = await blob.DownloadAsync(file.BlobPath, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);

        using var zip = new ZipArchive(new MemoryStream(ms.ToArray()), ZipArchiveMode.Read, leaveOpen: false);
        return zip.Entries
            .Where(e => !string.IsNullOrEmpty(e.Name) && ZipDataSetParser.DetectEntryFormat(e.FullName) == DataSetFormat.Xml)
            .Select(e => e.FullName)
            .OrderBy(p => p)
            .ToList();
    }

    public async Task<ExpressionPreviewDto> PreviewExpressionAsync(Guid fileId, string rowSelector, string? expr, CancellationToken ct)
    {
        var file = await db.DataSetFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId, ct)
            ?? throw new KeyNotFoundException($"DataSetFile {fileId} not found");

        // expr задан — предпросмотр относительного значения колонки (первые строки).
        // expr пуст — предпросмотр самого row-selector'а: сколько узлов и какие у них поля.
        var columnExpressionsJson = !string.IsNullOrWhiteSpace(expr)
            ? JsonSerializer.Serialize(new[] { new { name = "value", expr } })
            : null;

        var (schema, rowCount) = await ParseForDefinitionAsync(file.BlobPath, file.Format, rowSelector, columnExpressionsJson, ct);

        var samples = !string.IsNullOrWhiteSpace(expr)
            ? (IReadOnlyList<string>)(schema.FirstOrDefault()?.SampleValues.ToList() ?? [])
            : schema.Select(c => $"{c.Name}: {string.Join(", ", c.SampleValues)}").ToList();

        return new ExpressionPreviewDto(rowCount, samples);
    }

    public async Task<DataSetSourceDto?> SetSourceProcessingAsync(Guid sourceId, SetSourceProcessingInput input, CancellationToken ct)
    {
        var source = await db.DataSetSources.FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;

        source.SetProcessing(
            DataSetDtoMapper.SerializeJson(input.RowFilter), DataSetDtoMapper.SerializeJson(input.ComputedColumns), DataSetDtoMapper.SerializeJson(input.SortSpec));
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapSource(source);
    }

    public async Task<DataSetSourceDto?> ApplyProcessingTemplateAsync(Guid sourceId, Guid templateId, CancellationToken ct)
    {
        var source = await db.DataSetSources.Include(s => s.File).FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;

        var template = await db.DataSetProcessingTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId, ct)
            ?? throw new KeyNotFoundException($"DataSetProcessingTemplate {templateId} not found");

        // Extraction в шаблоне — опциональна: если задана, пере-парсим файл (имя источника не
        // трогаем — оно своё у каждого источника, не часть рецепта).
        if (!string.IsNullOrWhiteSpace(template.SheetOrPath))
        {
            var (schema, rowCount) = await ParseForDefinitionAsync(
                source.File.BlobPath, source.File.Format, template.SheetOrPath, template.ColumnExpressions, ct);
            source.UpdateDefinition(source.Name, template.SheetOrPath, template.ColumnExpressions);
            source.UpdateCache(DataSetDtoMapper.SerializeSchema(schema), rowCount);
        }
        source.SetProcessing(template.RowFilter, template.ComputedColumns, template.SortSpec);
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapSource(source);
    }
}
