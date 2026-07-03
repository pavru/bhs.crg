using System.IO.Compression;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Recognition;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.DataSets;

public class DataSetService(
    AppDbContext db,
    IBlobStorage blob,
    DataSetParserFactory parserFactory,
    IDocumentRecognizer recognizer,
    ILogger<DataSetService> logger,
    DataSetProcessingTemplateService processingTemplates,
    DataSetBindingTemplateService bindingTemplates
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
        return files.Select(DataSetDtoMapper.MapFile).ToList();
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

        return files.Select(DataSetDtoMapper.MapFile).ToList();
    }

    public async Task<DataSetFileDto> UploadFileAsync(UploadFileInput input, CancellationToken ct)
    {
        if (!Enum.TryParse<CatalogScope>(input.Scope, out var scope))
            throw new ArgumentException("Неверный scope");

        var format = DataSetDtoMapper.DetectFormat(input.FileName)
            ?? throw new ArgumentException("Неподдерживаемый формат файла");

        Guid? scopeId = scope != CatalogScope.System && Guid.TryParse(input.ScopeId, out var sid) ? sid : null;
        var name = string.IsNullOrWhiteSpace(input.Name) ? Path.GetFileNameWithoutExtension(input.FileName) : input.Name;

        await using var uploadStream = new MemoryStream(input.Bytes);
        var blobPath = await blob.UploadAsync(input.FileName, uploadStream, input.ContentType ?? "application/octet-stream", ct);

        var parser = parserFactory.GetParser(format);
        var sourceInfos = await parser.DetectSourcesAsync(input.Bytes, ct);

        var dataSetFile = DataSetFile.Create(name, format, blobPath, scope, scopeId);
        foreach (var info in sourceInfos)
            dataSetFile.AddSource(info.Name, info.SheetOrPath, DataSetDtoMapper.SerializeSchema(info.Columns), info.RowCount);

        db.DataSetFiles.Add(dataSetFile);
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapFile(dataSetFile);
    }

    public async Task<DataSetFileDto?> ReplaceFileAsync(Guid id, ReplaceFileInput input, CancellationToken ct)
    {
        var file = await db.DataSetFiles.Include(f => f.Sources).FirstOrDefaultAsync(f => f.Id == id, ct);
        if (file == null) return null;

        var format = DataSetDtoMapper.DetectFormat(input.FileName)
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
                existing.UpdateCache(DataSetDtoMapper.SerializeSchema(info.Columns), info.RowCount);
                updatedSourceIds.Add(existing.Id);
            }
            else
            {
                var added = file.AddSource(info.Name, info.SheetOrPath, DataSetDtoMapper.SerializeSchema(info.Columns), info.RowCount);
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
        return DataSetDtoMapper.MapFile(file);
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
            DataSetFormat.Pdf  => "application/pdf",
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
        return sources.Select(DataSetDtoMapper.MapSource).ToList();
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

    // Extraction для PDF не переиспользуемый XPath/JSONPath, а фиксированный профиль
    // (распознавание основной надписи каждой страницы) — SheetOrPath обязателен в домене,
    // но для PDF не несёт смысла локатора, только метка формата.
    private const string PdfRowSelector = "titleblock-registry";

    // Комплект чертежей может быть большим (десятки листов) — выше, чем MaxPages=10 у
    // PdfRasterizer (тот подобран под сертификаты/декларации, не трогаем).
    private const int PdfRecognizeMaxPages = 100;

    public async Task<DataSetSourceDto> CreatePdfSourceAsync(Guid fileId, CreatePdfSourceInput input, CancellationToken ct)
    {
        var file = await db.DataSetFiles.Include(f => f.Sources).FirstOrDefaultAsync(f => f.Id == fileId, ct)
            ?? throw new KeyNotFoundException($"DataSetFile {fileId} not found");
        if (file.Format != DataSetFormat.Pdf)
            throw new ArgumentException("Файл не в формате PDF.");

        var name = input.Name.Trim();

        if (input.Profile == PdfProfiles.Invoice)
        {
            // Профиль "Счёт на оплату" — один документ, шапка + вложенная таблица товаров.
            // Оба хранятся как отдельные DataSetSource под одним файлом (тот же паттерн, что и
            // у JSON/XML — один файл может иметь несколько источников), связаны маркерами в
            // SheetOrPath, не настоящей связью в БД — распознаётся и обновляется одним запросом.
            var header = file.AddSource(name, PdfProfiles.InvoiceHeaderMarker, "[]", 0);
            var lineItems = file.AddSource($"{name} — Товары", PdfProfiles.InvoiceLineItemsMarker, "[]", 0);
            db.DataSetSources.Add(header);
            db.DataSetSources.Add(lineItems);
            await db.SaveChangesAsync(ct);
            return DataSetDtoMapper.MapSource(header);
        }

        // Профиль "Основная надпись (ГОСТ Р 21.101-2020)" — тройка источников: обложка/титульный
        // лист/документы (последний — сгруппированный по Шифру реестр, с разрезанием исходного
        // PDF на под-файлы по группам, см. RecognizeGostSetAsync и GostPageGrouper). Тэги —
        // структурные метки (dataset.hasCover и т.п.), применимы ко всем трём.
        var tagsJson = input.Tags is { Count: > 0 } ? JsonSerializer.Serialize(input.Tags) : null;
        var cover = file.AddSource($"{name} — Обложка", PdfProfiles.GostCoverMarker, "[]", 0);
        var titlePage = file.AddSource($"{name} — Титульный лист", PdfProfiles.GostTitlePageMarker, "[]", 0);
        var documents = file.AddSource($"{name} — Документы", PdfProfiles.GostDocumentsMarker, "[]", 0);
        cover.SetTags(tagsJson);
        titlePage.SetTags(tagsJson);
        documents.SetTags(tagsJson);
        db.DataSetSources.Add(cover);
        db.DataSetSources.Add(titlePage);
        db.DataSetSources.Add(documents);
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapSource(documents);
    }

    public async Task<DataSetSourceDto?> RecognizePdfSourceAsync(Guid sourceId, CancellationToken ct)
    {
        var source = await db.DataSetSources.Include(s => s.File).FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;
        if (source.File.Format != DataSetFormat.Pdf)
            throw new ArgumentException("Источник не относится к PDF-файлу.");

        if (source.SheetOrPath is PdfProfiles.InvoiceHeaderMarker or PdfProfiles.InvoiceLineItemsMarker)
            return await RecognizeInvoiceAsync(source, sourceId, ct);

        if (source.SheetOrPath is PdfProfiles.GostCoverMarker or PdfProfiles.GostTitlePageMarker or PdfProfiles.GostDocumentsMarker)
            return await RecognizeGostSetAsync(source, sourceId, ct);

        // Дальше — legacy-путь для источников, созданных до тройки обложка/титул/документы
        // (маркер PdfRowSelector = "titleblock-registry") — постраничный плоский реестр без
        // группировки/разрезания, поведение не меняем.
        await using var stream = await blob.DownloadAsync(source.File.BlobPath, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        IReadOnlyList<byte[]> pages;
        try
        {
            pages = await Task.Run(
                () => PdfRasterizer.ToPngPages(bytes, PdfRasterizer.DefaultDpi, PdfRecognizeMaxPages), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ArgumentException($"Не удалось подготовить страницы PDF: {ex.Message}");
        }

        var fields = GostTitleBlockFields.All;
        var rows = new List<IReadOnlyDictionary<string, string?>>();
        for (var i = 0; i < pages.Count; i++)
        {
            try
            {
                var result = await recognizer.RecognizeAsync(
                    pages[i], "image/png", fields, RecognitionShared.BuildTitleBlockPrompt, ct: ct);
                rows.Add(result.Values);
            }
            catch (Exception ex) when (ex is RecognitionUnavailableException or RecognitionLimitException)
            {
                // Первая же страница — вероятно, движки не настроены вообще: нет смысла повторять
                // ту же ошибку ещё N-1 раз, сообщаем сразу. Дальше по комплекту — считаем
                // страницо-специфичной проблемой (не роняем весь реестр, см. фикс невычислимых
                // колонок XPath/JSONPath той же сессии), строка остаётся пустой.
                if (i == 0)
                    throw new ArgumentException($"Распознавание недоступно: {ex.Message}");
                logger.LogWarning(ex, "Распознавание страницы {Page} источника {SourceId} не удалось — строка останется пустой", i + 1, sourceId);
                rows.Add(fields.ToDictionary(f => f.Path, string? (f) => null));
            }
        }

        var columns = fields.Select(f => new DataSetColumnInfo(f.Path,
            rows.Take(3).Select(r => r.TryGetValue(f.Path, out var v) ? v ?? "" : "").ToArray()
        )).ToArray();

        source.UpdateCache(DataSetDtoMapper.SerializeSchema(columns), rows.Count, JsonSerializer.Serialize(rows));
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapSource(source);
    }

    /// <summary>
    /// Профиль "Счёт на оплату" — один вызов распознавания на весь многостраничный PDF
    /// (Gemini/Anthropic принимают application/pdf целиком без растеризации; Ollama растеризует
    /// сама внутри движка) вместо цикла по страницам. Результат расщепляется на пару источников:
    /// шапка (1 строка) и товары (N строк) — обе обновляются одним сохранением.
    /// </summary>
    private async Task<DataSetSourceDto?> RecognizeInvoiceAsync(DataSetSource source, Guid requestedSourceId, CancellationToken ct)
    {
        var header = source.SheetOrPath == PdfProfiles.InvoiceHeaderMarker
            ? source
            : await db.DataSetSources.FirstOrDefaultAsync(s => s.FileId == source.FileId && s.SheetOrPath == PdfProfiles.InvoiceHeaderMarker, ct);
        var lineItems = source.SheetOrPath == PdfProfiles.InvoiceLineItemsMarker
            ? source
            : await db.DataSetSources.FirstOrDefaultAsync(s => s.FileId == source.FileId && s.SheetOrPath == PdfProfiles.InvoiceLineItemsMarker, ct);
        if (header is null || lineItems is null)
            throw new ArgumentException("Не найдена пара источников «Счёт на оплату» (шапка/товары).");

        await using var stream = await blob.DownloadAsync(source.File.BlobPath, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        RecognitionResult result;
        try
        {
            result = await recognizer.RecognizeAsync(bytes, "application/pdf", InvoiceFields.All, RecognitionShared.BuildInvoicePrompt, ct: ct);
        }
        catch (Exception ex) when (ex is RecognitionUnavailableException or RecognitionLimitException)
        {
            throw new ArgumentException($"Распознавание недоступно: {ex.Message}");
        }

        var headerRow = InvoiceRecognitionSplitter.SplitHeader(result.Values);
        var headerColumns = InvoiceFields.HeaderFields
            .Select(f => new DataSetColumnInfo(f.Path, [headerRow.GetValueOrDefault(f.Path) ?? ""]))
            .ToArray();
        header.UpdateCache(DataSetDtoMapper.SerializeSchema(headerColumns), 1, JsonSerializer.Serialize(new[] { headerRow }));

        // Сломанный/не-JSON ответ модели по товарам — InvoiceRecognitionSplitter молча вернёт []
        // (шапка уже распозналась независимо, та же философия, что и у постраничного профиля).
        var lineItemRows = InvoiceRecognitionSplitter.SplitLineItems(result.Values);
        var lineItemColumns = InvoiceFields.LineItemColumns
            .Select(f => new DataSetColumnInfo(f.Path,
                lineItemRows.Take(3).Select(r => r.GetValueOrDefault(f.Path) ?? "").ToArray()))
            .ToArray();
        lineItems.UpdateCache(DataSetDtoMapper.SerializeSchema(lineItemColumns), lineItemRows.Count, JsonSerializer.Serialize(lineItemRows));

        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapSource(requestedSourceId == header.Id ? header : lineItems);
    }

    /// <summary>
    /// Профиль "gost-titleblock" (тройка) — тот же постраничный цикл распознавания, что и у
    /// legacy-реестра, но с классификатором ТипСтраницы (см. GostTitleBlockFields.AllWithPageType)
    /// и последующей маршрутизацией/группировкой (GostPageGrouper): обложка/титульный лист как
    /// есть, документы — сгруппированы по Шифру (не по НаименованиюДокумента — по ГОСТ Р
    /// 21.101-2020 форма 6, последующие листы и чертежей, и текстовых документов, обычно не
    /// повторяет наименование, но Шифр остаётся неизменным на всех листах документа) с
    /// разрезанием исходного PDF на под-файлы (PdfPageSplitter) для каждой группы.
    /// </summary>
    private async Task<DataSetSourceDto?> RecognizeGostSetAsync(DataSetSource source, Guid requestedSourceId, CancellationToken ct)
    {
        var cover = source.SheetOrPath == PdfProfiles.GostCoverMarker
            ? source
            : await db.DataSetSources.FirstOrDefaultAsync(s => s.FileId == source.FileId && s.SheetOrPath == PdfProfiles.GostCoverMarker, ct);
        var titlePage = source.SheetOrPath == PdfProfiles.GostTitlePageMarker
            ? source
            : await db.DataSetSources.FirstOrDefaultAsync(s => s.FileId == source.FileId && s.SheetOrPath == PdfProfiles.GostTitlePageMarker, ct);
        var documents = source.SheetOrPath == PdfProfiles.GostDocumentsMarker
            ? source
            : await db.DataSetSources.FirstOrDefaultAsync(s => s.FileId == source.FileId && s.SheetOrPath == PdfProfiles.GostDocumentsMarker, ct);
        if (cover is null || titlePage is null || documents is null)
            throw new ArgumentException("Не найдена тройка источников «обложка/титульный лист/документы».");

        await using var stream = await blob.DownloadAsync(source.File.BlobPath, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        IReadOnlyList<byte[]> pngPages;
        try
        {
            pngPages = await Task.Run(
                () => PdfRasterizer.ToPngPages(bytes, PdfRasterizer.DefaultDpi, PdfRecognizeMaxPages), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ArgumentException($"Не удалось подготовить страницы PDF: {ex.Message}");
        }

        var fields = GostTitleBlockFields.AllWithPageType;
        var rows = new List<IReadOnlyDictionary<string, string?>>();
        for (var i = 0; i < pngPages.Count; i++)
        {
            try
            {
                var result = await recognizer.RecognizeAsync(
                    pngPages[i], "image/png", fields, RecognitionShared.BuildTitleBlockPrompt, ct: ct);
                rows.Add(result.Values);
            }
            catch (Exception ex) when (ex is RecognitionUnavailableException or RecognitionLimitException)
            {
                if (i == 0)
                    throw new ArgumentException($"Распознавание недоступно: {ex.Message}");
                logger.LogWarning(ex, "Распознавание страницы {Page} источника {SourceId} не удалось — строка останется пустой", i + 1, requestedSourceId);
                rows.Add(fields.ToDictionary(f => f.Path, string? (f) => null));
            }
        }

        var grouping = GostPageGrouper.Group(rows);
        var baseColumnPaths = GostTitleBlockFields.All.Select(f => f.Path).ToArray();

        static DataSetColumnInfo[] BuildColumns(IReadOnlyList<string> columnPaths, IReadOnlyList<IReadOnlyDictionary<string, string?>> data) =>
            columnPaths.Select(p => new DataSetColumnInfo(p,
                data.Take(3).Select(r => r.TryGetValue(p, out var v) ? v ?? "" : "").ToArray())).ToArray();

        cover.UpdateCache(DataSetDtoMapper.SerializeSchema(BuildColumns(baseColumnPaths, grouping.Cover)), grouping.Cover.Count, JsonSerializer.Serialize(grouping.Cover));
        titlePage.UpdateCache(DataSetDtoMapper.SerializeSchema(BuildColumns(baseColumnPaths, grouping.TitlePage)), grouping.TitlePage.Count, JsonSerializer.Serialize(grouping.TitlePage));

        var documentRows = new List<Dictionary<string, string?>>();
        foreach (var group in grouping.Documents)
        {
            var row = new Dictionary<string, string?>(group.Fields);
            // Имя файла — по названию документа, если распознано (обычно только на первом/титульном
            // листе — форма 5/3), иначе по шифру (group.Code — присутствует и на форме 6, см. GostPageGrouper).
            var displayName = row.GetValueOrDefault("НаименованиеДокумента");
            var fileLabel = string.IsNullOrWhiteSpace(displayName) ? group.Code : displayName;
            try
            {
                var splitBytes = PdfPageSplitter.ExtractPages(bytes, group.PageIndices);
                var fileName = $"{SanitizeFileName(fileLabel)}.pdf";
                using var splitStream = new MemoryStream(splitBytes);
                row["ФайлПуть"] = await blob.UploadAsync(fileName, splitStream, "application/pdf", ct);
                row["РазмерБайт"] = splitBytes.Length.ToString();
            }
            catch (Exception ex)
            {
                // Не удалось разрезать конкретную группу — строка реестра остаётся без файла,
                // остальные группы и вся операция не падают (та же философия отказоустойчивости).
                logger.LogWarning(ex, "Не удалось разрезать PDF для документа «{DocumentLabel}» источника {SourceId}", fileLabel, documents.Id);
            }
            documentRows.Add(row);
        }

        var documentsColumnPaths = baseColumnPaths.Concat(["КоличествоЛистов", "ФайлПуть", "РазмерБайт"]).ToArray();
        documents.UpdateCache(DataSetDtoMapper.SerializeSchema(BuildColumns(documentsColumnPaths, documentRows)), documentRows.Count, JsonSerializer.Serialize(documentRows));

        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapSource(requestedSourceId == cover.Id ? cover : requestedSourceId == titlePage.Id ? titlePage : documents);
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "документ" : sanitized;
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

    // ── Processing templates ─── delegated to DataSetProcessingTemplateService ────

    public Task<IReadOnlyList<DataSetProcessingTemplateDto>> ListProcessingTemplatesAsync(CancellationToken ct) =>
        processingTemplates.ListAsync(ct);

    public Task<DataSetProcessingTemplateDto> CreateProcessingTemplateAsync(
        CreateProcessingTemplateInput input, CancellationToken ct) =>
        processingTemplates.CreateAsync(input, ct);

    public Task<DataSetProcessingTemplateDto?> UpdateProcessingTemplateAsync(
        Guid id, UpdateProcessingTemplateInput input, CancellationToken ct) =>
        processingTemplates.UpdateAsync(id, input, ct);

    public Task<bool> DeleteProcessingTemplateAsync(Guid id, CancellationToken ct) =>
        processingTemplates.DeleteAsync(id, ct);

    // ── Bindings ────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DataSetBindingDto>> ListBindingsAsync(Guid? instanceId, Guid? commonDataEntryId, CancellationToken ct)
    {
        var bindings = await db.DataSetBindings
            .Include(b => b.Source).ThenInclude(s => s.File)
            .Where(b => (instanceId != null && b.InstanceId == instanceId)
                     || (commonDataEntryId != null && b.CommonDataEntryId == commonDataEntryId))
            .AsNoTracking()
            .ToListAsync(ct);
        return bindings.Select(DataSetDtoMapper.MapBinding).ToList();
    }

    public async Task<DataSetBindingDto?> CreateBindingAsync(CreateBindingInput input, CancellationToken ct)
    {
        if ((input.InstanceId is null) == (input.CommonDataEntryId is null))
            throw new ArgumentException("Ровно один из InstanceId/CommonDataEntryId должен быть задан");

        var source = await db.DataSetSources.Include(s => s.File)
            .FirstOrDefaultAsync(s => s.Id == input.SourceId, ct);
        if (source == null) return null;

        var binding = input.InstanceId is not null
            ? DataSetBinding.ForInstance(input.InstanceId.Value, input.SourceId, input.TargetFieldKey, DataSetDtoMapper.SerializeMapping(input.Mapping))
            : DataSetBinding.ForCommonDataEntry(input.CommonDataEntryId!.Value, input.SourceId, input.TargetFieldKey, DataSetDtoMapper.SerializeMapping(input.Mapping));
        db.DataSetBindings.Add(binding);
        await db.SaveChangesAsync(ct);

        await db.Entry(binding).Reference(b => b.Source).LoadAsync(ct);
        await db.Entry(binding.Source).Reference(s => s.File).LoadAsync(ct);
        return DataSetDtoMapper.MapBinding(binding);
    }

    public async Task<DataSetBindingDto?> UpdateBindingAsync(Guid id, UpdateBindingInput input, CancellationToken ct)
    {
        var binding = await db.DataSetBindings.Include(b => b.Source).ThenInclude(s => s.File)
            .FirstOrDefaultAsync(b => b.Id == id, ct);
        if (binding == null) return null;

        binding.Update(input.TargetFieldKey, DataSetDtoMapper.SerializeMapping(input.Mapping));
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapBinding(binding);
    }

    public async Task<bool> DeleteBindingAsync(Guid id, CancellationToken ct)
    {
        var binding = await db.DataSetBindings.FindAsync([id], ct);
        if (binding == null) return false;
        db.DataSetBindings.Remove(binding);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<BindingPreviewDto>> PreviewBindingsAsync(Guid? instanceId, Guid? commonDataEntryId, CancellationToken ct)
    {
        var bindings = await db.DataSetBindings
            .Include(b => b.Source).ThenInclude(s => s.File)
            .Where(b => (instanceId != null && b.InstanceId == instanceId)
                     || (commonDataEntryId != null && b.CommonDataEntryId == commonDataEntryId))
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
                    var data = new Dictionary<string, object?>();
                    foreach (var (fieldKey, colName) in mapping)
                        if (!string.IsNullOrEmpty(colName))
                            data[fieldKey] = DataSetDtoMapper.PreviewCell(colName, row);

                    results.Add(new BindingPreviewDto(binding.Id, binding.Source.Name, binding.Source.File.Name,
                        "scalar", null, rows.Count, data, null));
                }
                else
                {
                    var mapped = rows.Select(row =>
                    {
                        var obj = new Dictionary<string, object?>();
                        foreach (var (fieldKey, colName) in mapping)
                            if (!string.IsNullOrEmpty(colName))
                                obj[fieldKey] = DataSetDtoMapper.PreviewCell(colName, row);
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

    // ── Binding templates ─── delegated to DataSetBindingTemplateService ──────────

    public Task<IReadOnlyList<DataSetBindingTemplateDto>> ListTemplatesAsync(Guid docTypeId, CancellationToken ct) =>
        bindingTemplates.ListAsync(docTypeId, ct);

    public Task<DataSetBindingTemplateDto> CreateTemplateAsync(Guid docTypeId, CreateTemplateInput input, CancellationToken ct) =>
        bindingTemplates.CreateAsync(docTypeId, input, ct);

    public Task<DataSetBindingTemplateDto?> UpdateTemplateAsync(
        Guid docTypeId, Guid id, UpdateTemplateInput input, CancellationToken ct) =>
        bindingTemplates.UpdateAsync(docTypeId, id, input, ct);

    public Task<bool> DeleteTemplateAsync(Guid docTypeId, Guid id, CancellationToken ct) =>
        bindingTemplates.DeleteAsync(docTypeId, id, ct);

}
