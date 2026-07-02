using System.IO.Compression;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.DataSets;

public class DataSetService(
    AppDbContext db,
    IBlobStorage blob,
    DataSetParserFactory parserFactory,
    ILogger<DataSetService> logger
) : IDataSetService
{
    private record CachedColumnInfo(string Name, string[] SampleValues);

    // cachedSchema stores camelCase keys ("name"/"sampleValues") — match them case-insensitively.
    private static readonly JsonSerializerOptions CachedSchemaJson = new() { PropertyNameCaseInsensitive = true };

    // ── Files ───────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DataSetFileDto>> ListFilesAsync(string? scope, Guid? scopeId, CancellationToken ct)
    {
        var q = db.DataSetFiles.Include(f => f.Sources).AsNoTracking().AsQueryable();
        if (scope != null && Enum.TryParse<CatalogScope>(scope, out var s))
            q = q.Where(f => f.Scope == s && f.ScopeId == scopeId);

        var files = await q.OrderBy(f => f.Name).ToListAsync(ct);
        return files.Select(MapFile).ToList();
    }

    public async Task<IReadOnlyList<DataSetFileDto>> ListAvailableFilesAsync(Guid setId, CancellationToken ct)
    {
        var set = await db.Set<DocumentSet>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == setId, ct)
            ?? throw new KeyNotFoundException("DocumentSet не найден");
        var section = await db.Set<Section>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == set.SectionId, ct);

        var files = await db.DataSetFiles
            .Include(f => f.Sources)
            .AsNoTracking()
            .Where(f =>
                (f.Scope == CatalogScope.System && f.ScopeId == null) ||
                (f.Scope == CatalogScope.Set && f.ScopeId == setId) ||
                (section != null && f.Scope == CatalogScope.Section && f.ScopeId == section.Id) ||
                (section != null && f.Scope == CatalogScope.Construction && f.ScopeId == section.ConstructionId))
            .OrderBy(f => f.Scope).ThenBy(f => f.Name)
            .ToListAsync(ct);

        return files.Select(MapFile).ToList();
    }

    public async Task<DataSetFileDto> UploadFileAsync(UploadFileInput input, CancellationToken ct)
    {
        if (!Enum.TryParse<CatalogScope>(input.Scope, out var scope))
            throw new ArgumentException("Неверный scope");

        var format = DetectFormat(input.FileName)
            ?? throw new ArgumentException("Неподдерживаемый формат файла");

        Guid? scopeId = scope != CatalogScope.System && Guid.TryParse(input.ScopeId, out var sid) ? sid : null;
        var name = string.IsNullOrWhiteSpace(input.Name) ? Path.GetFileNameWithoutExtension(input.FileName) : input.Name;

        await using var uploadStream = new MemoryStream(input.Bytes);
        var blobPath = await blob.UploadAsync(input.FileName, uploadStream, input.ContentType ?? "application/octet-stream", ct);

        var parser = parserFactory.GetParser(format);
        var sourceInfos = await parser.DetectSourcesAsync(input.Bytes, ct);

        var dataSetFile = DataSetFile.Create(name, format, blobPath, scope, scopeId);
        foreach (var info in sourceInfos)
            dataSetFile.AddSource(info.Name, info.SheetOrPath, SerializeSchema(info.Columns), info.RowCount);

        db.DataSetFiles.Add(dataSetFile);
        await db.SaveChangesAsync(ct);
        return MapFile(dataSetFile);
    }

    public async Task<DataSetFileDto?> ReplaceFileAsync(Guid id, ReplaceFileInput input, CancellationToken ct)
    {
        var file = await db.DataSetFiles.Include(f => f.Sources).FirstOrDefaultAsync(f => f.Id == id, ct);
        if (file == null) return null;

        var format = DetectFormat(input.FileName)
            ?? throw new ArgumentException("Неподдерживаемый формат файла");

        try { await blob.DeleteAsync(file.BlobPath, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "Не удалось удалить старый blob при замене файла {FileId}", id); }

        await using var uploadStream = new MemoryStream(input.Bytes);
        var newBlobPath = await blob.UploadAsync(input.FileName, uploadStream, input.ContentType ?? "application/octet-stream", ct);

        var parser = parserFactory.GetParser(format);
        var sourceInfos = await parser.DetectSourcesAsync(input.Bytes, ct);

        // Match existing sources by sheetOrPath (then name) to preserve bindings.
        var updatedSourceIds = new HashSet<Guid>();
        foreach (var info in sourceInfos)
        {
            var existing = file.Sources.FirstOrDefault(s => s.SheetOrPath == info.SheetOrPath)
                ?? file.Sources.FirstOrDefault(s => s.Name == info.Name);
            if (existing != null)
            {
                existing.UpdateCache(SerializeSchema(info.Columns), info.RowCount);
                updatedSourceIds.Add(existing.Id);
            }
            else
            {
                var added = file.AddSource(info.Name, info.SheetOrPath, SerializeSchema(info.Columns), info.RowCount);
                // file уже отслеживается — см. пояснение в CreateSourceAsync (иначе Modified вместо Added).
                db.DataSetSources.Add(added);
                updatedSourceIds.Add(added.Id);
            }
        }

        // Drop sources no longer present in the file, unless they still have bindings.
        foreach (var src in file.Sources.Where(s => !updatedSourceIds.Contains(s.Id)).ToList())
        {
            var hasBindings = await db.DataSetBindings.AnyAsync(b => b.SourceId == src.Id, ct);
            if (!hasBindings) db.DataSetSources.Remove(src);
        }

        file.UpdateBlobPath(newBlobPath, format);
        if (!string.IsNullOrWhiteSpace(input.Name)) file.UpdateName(input.Name);

        await db.SaveChangesAsync(ct);
        return MapFile(file);
    }

    public async Task<FileDownloadDto?> DownloadFileAsync(Guid id, CancellationToken ct)
    {
        var file = await db.DataSetFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct);
        if (file == null) return null;

        // Original extension from blobPath (format: bucket/yyyy/MM/dd/{guid}_{originalName}).
        var blobFileName = file.BlobPath.Split('/').Last();
        var underscoreIdx = blobFileName.IndexOf('_');
        var originalName = underscoreIdx >= 0 ? blobFileName[(underscoreIdx + 1)..] : blobFileName;
        var originalExt = Path.GetExtension(originalName);
        var downloadName = string.IsNullOrEmpty(originalExt) ? file.Name : $"{file.Name}{originalExt}";

        var contentType = file.Format switch
        {
            DataSetFormat.Csv  => "text/csv",
            DataSetFormat.Xlsx => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            DataSetFormat.Xls  => "application/vnd.ms-excel",
            DataSetFormat.Xml  => "application/xml",
            DataSetFormat.Json => "application/json",
            DataSetFormat.Zip  => "application/zip",
            _                  => "application/octet-stream",
        };

        var stream = await blob.DownloadAsync(file.BlobPath, ct);
        return new FileDownloadDto(stream, contentType, downloadName);
    }

    public async Task<bool> DeleteFileAsync(Guid id, CancellationToken ct)
    {
        var file = await db.DataSetFiles.FindAsync([id], ct);
        if (file == null) return false;

        try { await blob.DeleteAsync(file.BlobPath, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "Не удалось удалить blob при удалении файла {FileId}", id); }

        db.DataSetFiles.Remove(file);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── Sources ─────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DataSetSourceDto>> ListSourcesAsync(Guid fileId, CancellationToken ct)
    {
        var sources = await db.DataSetSources.Where(s => s.FileId == fileId).AsNoTracking().ToListAsync(ct);
        return sources.Select(MapSource).ToList();
    }

    public async Task<SourcePreviewDto?> PreviewSourceAsync(Guid sourceId, int maxRows, CancellationToken ct)
    {
        var source = await db.DataSetSources.Include(s => s.File).Include(s => s.ProcessingTemplate).AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;

        await using var stream = await blob.DownloadAsync(source.File.BlobPath, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var parser = parserFactory.GetParser(source.File.Format);
        var result = await parser.ParseAsync(ms.ToArray(), source.SheetOrPath, source.ColumnExpressions, ct);

        var (rowFilter, computedColumns, sortSpec) = DataSetBindingProcessor.ResolveProcessing(source);
        var rows = DataSetComputedColumnExecutor.Apply(computedColumns, result.Rows.ToList());
        rows = DataSetRowFilterExecutor.Apply(rowFilter, rows);
        rows = DataSetSortExecutor.Apply(sortSpec, rows);

        var take = maxRows <= 0 ? 50 : maxRows;
        var columns = result.Columns.Select(c => c.Name).ToList();
        // Вычисляемые колонки могут добавить новые имена, которых нет в исходном разборе.
        columns.AddRange(rows.SelectMany(r => r.Keys).Distinct().Except(columns));

        var previewRows = rows.Take(take)
            .Select(r => (IReadOnlyList<string?>)columns.Select(c => r.TryGetValue(c, out var v) ? v : null).ToList())
            .ToList();
        return new SourcePreviewDto(columns, previewRows, rows.Count);
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

        var columnExpressionsJson = SerializeColumnExpressions(input.ColumnExpressions);
        var (schema, rowCount) = await ParseForDefinitionAsync(file.BlobPath, file.Format, input.SheetOrPath, columnExpressionsJson, ct);

        var source = file.AddSource(input.Name.Trim(), input.SheetOrPath.Trim(), SerializeSchema(schema), rowCount, columnExpressionsJson);
        // file уже отслеживается (загружен из БД) — новый дочерний источник, добавленный в его
        // коллекцию навигации, EF не распознаёт как Added автоматически (Guid — клиентский ключ,
        // не default-значение), поэтому без явного Add() трекер помечает его Modified и
        // пытается сделать UPDATE несуществующей строки → DbUpdateConcurrencyException.
        db.DataSetSources.Add(source);
        await db.SaveChangesAsync(ct);
        return MapSource(source);
    }

    public async Task<DataSetSourceDto?> UpdateSourceAsync(Guid sourceId, UpdateSourceInput input, CancellationToken ct)
    {
        var source = await db.DataSetSources.Include(s => s.File).FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;

        var columnExpressionsJson = SerializeColumnExpressions(input.ColumnExpressions);
        var (schema, rowCount) = await ParseForDefinitionAsync(
            source.File.BlobPath, source.File.Format, input.SheetOrPath, columnExpressionsJson, ct);

        source.UpdateDefinition(input.Name.Trim(), input.SheetOrPath.Trim(), columnExpressionsJson);
        source.UpdateCache(SerializeSchema(schema), rowCount);
        await db.SaveChangesAsync(ct);
        return MapSource(source);
    }

    public async Task<bool> DeleteSourceAsync(Guid sourceId, CancellationToken ct)
    {
        var source = await db.DataSetSources.FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return false;

        var hasBindings = await db.DataSetBindings.AnyAsync(b => b.SourceId == sourceId, ct);
        if (hasBindings)
            throw new InvalidOperationException("Источник используется в привязках документов — сначала удалите привязки.");

        db.DataSetSources.Remove(source);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // Копия источника на том же файле — тот же locator/колонки/обработка (Filter/Conversion/Sort,
    // включая ссылку на шаблон), но независимая: правки одной копии не затрагивают другую.
    // Позволяет получить несколько наборов на основе одного файла без переопределения extraction
    // с нуля (актуально и для форматов без ручного builder'а — CSV/XLSX — где нужно только
    // разное Filter/Conversion/Sort поверх одинаковых данных).
    public async Task<DataSetSourceDto?> DuplicateSourceAsync(Guid sourceId, CancellationToken ct)
    {
        var source = await db.DataSetSources.Include(s => s.File).FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;

        var copy = source.File.AddSource(
            $"{source.Name} (копия)", source.SheetOrPath, source.CachedSchema, source.CachedRowCount, source.ColumnExpressions);
        copy.SetProcessing(source.RowFilter, source.ComputedColumns, source.SortSpec, source.ProcessingTemplateId);
        // file уже отслеживается — см. пояснение в CreateSourceAsync (иначе Modified вместо Added).
        db.DataSetSources.Add(copy);
        await db.SaveChangesAsync(ct);
        return MapSource(copy);
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

    private static string? SerializeColumnExpressions(IReadOnlyList<ColumnExprDto>? columnExpressions) =>
        columnExpressions is { Count: > 0 }
            ? JsonSerializer.Serialize(columnExpressions.Select(c => new { name = c.Name, expr = c.Expr }))
            : null;

    public async Task<DataSetSourceDto?> SetSourceProcessingAsync(Guid sourceId, SetSourceProcessingInput input, CancellationToken ct)
    {
        var source = await db.DataSetSources.FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;

        if (input.ProcessingTemplateId is { } templateId
            && !await db.DataSetProcessingTemplates.AnyAsync(t => t.Id == templateId, ct))
            throw new ArgumentException("Шаблон обработки не найден");

        source.SetProcessing(
            SerializeJson(input.RowFilter), SerializeJson(input.ComputedColumns),
            SerializeJson(input.SortSpec), input.ProcessingTemplateId);
        await db.SaveChangesAsync(ct);
        return MapSource(source);
    }

    // ── Processing templates ───────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DataSetProcessingTemplateDto>> ListProcessingTemplatesAsync(CancellationToken ct)
    {
        var templates = await db.DataSetProcessingTemplates.OrderBy(t => t.Name).AsNoTracking().ToListAsync(ct);
        return templates.Select(MapProcessingTemplate).ToList();
    }

    public async Task<DataSetProcessingTemplateDto> CreateProcessingTemplateAsync(
        CreateProcessingTemplateInput input, CancellationToken ct)
    {
        var template = DataSetProcessingTemplate.Create(
            input.Name, SerializeJson(input.RowFilter), SerializeJson(input.ComputedColumns), SerializeJson(input.SortSpec));
        db.DataSetProcessingTemplates.Add(template);
        await db.SaveChangesAsync(ct);
        return MapProcessingTemplate(template);
    }

    public async Task<DataSetProcessingTemplateDto?> UpdateProcessingTemplateAsync(
        Guid id, UpdateProcessingTemplateInput input, CancellationToken ct)
    {
        var template = await db.DataSetProcessingTemplates.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (template == null) return null;

        template.Update(input.Name, SerializeJson(input.RowFilter), SerializeJson(input.ComputedColumns), SerializeJson(input.SortSpec));
        await db.SaveChangesAsync(ct);
        return MapProcessingTemplate(template);
    }

    public async Task<bool> DeleteProcessingTemplateAsync(Guid id, CancellationToken ct)
    {
        var template = await db.DataSetProcessingTemplates.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (template == null) return false;
        db.DataSetProcessingTemplates.Remove(template);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── Bindings ────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DataSetBindingDto>> ListBindingsAsync(Guid instanceId, CancellationToken ct)
    {
        var bindings = await db.DataSetBindings
            .Include(b => b.Source).ThenInclude(s => s.File)
            .Where(b => b.InstanceId == instanceId)
            .AsNoTracking()
            .ToListAsync(ct);
        return bindings.Select(MapBinding).ToList();
    }

    public async Task<DataSetBindingDto?> CreateBindingAsync(CreateBindingInput input, CancellationToken ct)
    {
        var source = await db.DataSetSources.Include(s => s.File)
            .FirstOrDefaultAsync(s => s.Id == input.SourceId, ct);
        if (source == null) return null;

        var binding = DataSetBinding.Create(
            input.InstanceId, input.SourceId, input.TargetFieldKey, SerializeMapping(input.Mapping));
        db.DataSetBindings.Add(binding);
        await db.SaveChangesAsync(ct);

        await db.Entry(binding).Reference(b => b.Source).LoadAsync(ct);
        await db.Entry(binding.Source).Reference(s => s.File).LoadAsync(ct);
        return MapBinding(binding);
    }

    public async Task<DataSetBindingDto?> UpdateBindingAsync(Guid id, UpdateBindingInput input, CancellationToken ct)
    {
        var binding = await db.DataSetBindings.Include(b => b.Source).ThenInclude(s => s.File)
            .FirstOrDefaultAsync(b => b.Id == id, ct);
        if (binding == null) return null;

        binding.Update(input.TargetFieldKey, SerializeMapping(input.Mapping));
        await db.SaveChangesAsync(ct);
        return MapBinding(binding);
    }

    public async Task<bool> DeleteBindingAsync(Guid id, CancellationToken ct)
    {
        var binding = await db.DataSetBindings.FindAsync([id], ct);
        if (binding == null) return false;
        db.DataSetBindings.Remove(binding);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<BindingPreviewDto>> PreviewBindingsAsync(Guid instanceId, CancellationToken ct)
    {
        var bindings = await db.DataSetBindings
            .Include(b => b.Source).ThenInclude(s => s.File)
            .Include(b => b.Source).ThenInclude(s => s.ProcessingTemplate)
            .Where(b => b.InstanceId == instanceId)
            .AsNoTracking()
            .ToListAsync(ct);

        var results = new List<BindingPreviewDto>();
        foreach (var binding in bindings)
        {
            try
            {
                var rows = await DataSetBindingProcessor.LoadRowsAsync(blob, parserFactory, binding.Source, ct);

                var mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(binding.Mapping) ?? [];

                if (binding.TargetFieldKey is null)
                {
                    var row = rows.Count > 0 ? rows[0] : null;
                    var data = new Dictionary<string, string?>();
                    foreach (var (fieldKey, colName) in mapping)
                        if (!string.IsNullOrEmpty(colName))
                            data[fieldKey] = PreviewCell(colName, row);

                    results.Add(new BindingPreviewDto(binding.Id, binding.Source.Name, binding.Source.File.Name,
                        "scalar", null, rows.Count, data, null));
                }
                else
                {
                    var mapped = rows.Select(row =>
                    {
                        var obj = new Dictionary<string, string?>();
                        foreach (var (fieldKey, colName) in mapping)
                            if (!string.IsNullOrEmpty(colName))
                                obj[fieldKey] = PreviewCell(colName, row);
                        return obj;
                    }).ToList();

                    results.Add(new BindingPreviewDto(binding.Id, binding.Source.Name, binding.Source.File.Name,
                        "tabular", binding.TargetFieldKey, mapped.Count, mapped, null));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Не удалось построить предпросмотр привязки {BindingId}", binding.Id);
                results.Add(new BindingPreviewDto(binding.Id, binding.Source?.Name ?? "?",
                    binding.Source?.File?.Name ?? "?", "error", binding.TargetFieldKey, 0, new { }, ex.Message));
            }
        }
        return results;
    }

    // ── Binding templates ─────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DataSetBindingTemplateDto>> ListTemplatesAsync(Guid docTypeId, CancellationToken ct)
    {
        var templates = await db.DataSetBindingTemplates
            .Where(t => t.DocumentTypeId == docTypeId)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .AsNoTracking()
            .ToListAsync(ct);
        return templates.Select(MapTemplate).ToList();
    }

    public async Task<DataSetBindingTemplateDto> CreateTemplateAsync(Guid docTypeId, CreateTemplateInput input, CancellationToken ct)
    {
        var maxOrder = await db.DataSetBindingTemplates
            .Where(t => t.DocumentTypeId == docTypeId)
            .MaxAsync(t => (int?)t.SortOrder, ct) ?? -1;

        var template = DataSetBindingTemplate.Create(
            docTypeId, input.Name, input.TargetFieldKey, SerializeMapping(input.ColumnMappings), maxOrder + 1);

        db.DataSetBindingTemplates.Add(template);
        await db.SaveChangesAsync(ct);
        return MapTemplate(template);
    }

    public async Task<DataSetBindingTemplateDto?> UpdateTemplateAsync(
        Guid docTypeId, Guid id, UpdateTemplateInput input, CancellationToken ct)
    {
        var template = await db.DataSetBindingTemplates
            .FirstOrDefaultAsync(t => t.Id == id && t.DocumentTypeId == docTypeId, ct);
        if (template == null) return null;

        template.Update(input.Name, input.TargetFieldKey, SerializeMapping(input.ColumnMappings),
            input.SortOrder ?? template.SortOrder);
        await db.SaveChangesAsync(ct);
        return MapTemplate(template);
    }

    public async Task<bool> DeleteTemplateAsync(Guid docTypeId, Guid id, CancellationToken ct)
    {
        var template = await db.DataSetBindingTemplates
            .FirstOrDefaultAsync(t => t.Id == id && t.DocumentTypeId == docTypeId, ct);
        if (template == null) return false;
        db.DataSetBindingTemplates.Remove(template);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static DataSetFormat? DetectFormat(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".csv" or ".txt"  => DataSetFormat.Csv,
            ".xlsx"           => DataSetFormat.Xlsx,
            ".xls"            => DataSetFormat.Xls,
            ".xml"            => DataSetFormat.Xml,
            ".json"           => DataSetFormat.Json,
            ".zip" or ".gsfx" => DataSetFormat.Zip,
            _                 => null,
        };

    private static string SerializeSchema(IReadOnlyList<DataSetColumnInfo> columns) =>
        JsonSerializer.Serialize(columns.Select(c => new { name = c.Name, sampleValues = c.SampleValues }));

    private static string SerializeMapping(Dictionary<string, string>? mapping) =>
        JsonSerializer.Serialize(mapping ?? new Dictionary<string, string>());

    // Значение ячейки для предпросмотра. Для ссылочного маппинга (@@ref) показываем
    // искомое значение колонки с маркером — фактический резолвинг в каталог выполняется
    // при генерации.
    private static string? PreviewCell(string mapVal, IReadOnlyDictionary<string, string?>? row)
    {
        var refMap = DataSetMappingValue.ParseRef(mapVal);
        if (refMap is not null)
        {
            var v = row != null && row.TryGetValue(refMap.Column, out var lv) ? lv : null;
            return string.IsNullOrWhiteSpace(v) ? null : $"🔗 {v}";
        }
        return row != null && row.TryGetValue(mapVal, out var val) ? val : null;
    }

    private static string? SerializeJson(object? value) =>
        value is null ? null : JsonSerializer.Serialize(value);

    private static object? DeserializeJson(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<object>(json);

    private static DataSetFileDto MapFile(DataSetFile f) => new(
        f.Id, f.Name, f.Format.ToString(), f.Scope.ToString(), f.ScopeId,
        f.Sources.Select(MapSource).ToList(), f.CreatedAt);

    private static DataSetSourceDto MapSource(DataSetSource s) => new(
        s.Id, s.FileId, s.Name, s.SheetOrPath, s.ColumnExpressions, s.CachedSchema, s.CachedRowCount,
        DeserializeJson(s.RowFilter), DeserializeJson(s.ComputedColumns), DeserializeJson(s.SortSpec),
        s.ProcessingTemplateId);

    private static DataSetBindingDto MapBinding(DataSetBinding b) => new(
        b.Id, b.InstanceId, b.SourceId, b.TargetFieldKey,
        JsonSerializer.Deserialize<Dictionary<string, string>>(b.Mapping) ?? [],
        b.Source is null ? null : new BindingSourceDto(
            b.Source.Id, b.Source.Name, b.Source.SheetOrPath, b.Source.CachedSchema, b.Source.CachedRowCount,
            b.Source.File is null ? null : new BindingFileDto(
                b.Source.File.Id, b.Source.File.Name, b.Source.File.Format.ToString(),
                b.Source.File.Scope.ToString(), b.Source.File.ScopeId)));

    private static DataSetBindingTemplateDto MapTemplate(DataSetBindingTemplate t) => new(
        t.Id, t.DocumentTypeId, t.Name, t.TargetFieldKey,
        JsonSerializer.Deserialize<Dictionary<string, string>>(t.ColumnMappings) ?? [],
        t.SortOrder, t.CreatedAt, t.UpdatedAt);

    private static DataSetProcessingTemplateDto MapProcessingTemplate(DataSetProcessingTemplate t) => new(
        t.Id, t.Name, DeserializeJson(t.RowFilter), DeserializeJson(t.ComputedColumns), DeserializeJson(t.SortSpec),
        t.CreatedAt, t.UpdatedAt);
}
