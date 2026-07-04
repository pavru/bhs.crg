using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Recognition;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Создание и распознавание PDF-источников (профили "Счёт на оплату" и ГОСТ Р 21.101-2020
/// "Основная надпись") — растеризация/vision-LLM/группировка/физическое разрезание. Вынесено из
/// <see cref="DataSetService"/> (четвёртый шаг декомпозиции God Object'а, сделан внеочерёдно —
/// раньше запланированного места в очереди, т.к. фича ручной корректировки разбиения PDF целиком
/// принадлежит этому сервису по зависимостям; см. архитектурный отчёт, «Ручная корректировка
/// разбиения PDF»).
/// </summary>
public class DataSetPdfRecognitionService(
    AppDbContext db,
    IBlobStorage blob,
    IDocumentRecognizer recognizer,
    ILogger<DataSetPdfRecognitionService> logger
)
{
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

    public async Task<DataSetSourceDto?> RecognizePdfSourceAsync(Guid sourceId, bool confirm, CancellationToken ct)
    {
        var source = await db.DataSetSources.Include(s => s.File).FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;
        if (source.File.Format != DataSetFormat.Pdf)
            throw new ArgumentException("Источник не относится к PDF-файлу.");

        if (source.SheetOrPath is PdfProfiles.InvoiceHeaderMarker or PdfProfiles.InvoiceLineItemsMarker)
            return await RecognizeInvoiceAsync(source, sourceId, ct);

        if (source.SheetOrPath is PdfProfiles.GostCoverMarker or PdfProfiles.GostTitlePageMarker or PdfProfiles.GostDocumentsMarker)
        {
            // Ручная правка группировки — дороже автораспознавания LLM-вызовов (пользователь
            // руками разбирал документы) — не затираем без явного согласия.
            var documentsSource = source.SheetOrPath == PdfProfiles.GostDocumentsMarker
                ? source
                : await db.DataSetSources.FirstOrDefaultAsync(s => s.FileId == source.FileId && s.SheetOrPath == PdfProfiles.GostDocumentsMarker, ct);
            var existingGrouping = ParseGrouping(documentsSource?.GostGrouping);
            if (existingGrouping is { ManuallyEdited: true } && !confirm)
                throw new InvalidOperationException(
                    "Разбиение этого источника было скорректировано вручную — повторное распознавание сотрёт ручные правки. Подтвердите, чтобы продолжить.");

            return await RecognizeGostSetAsync(source, sourceId, ct);
        }

        // Дальше — legacy-путь для источников, созданных до тройки обложка/титул/документы
        // (маркер "titleblock-registry") — постраничный плоский реестр без группировки/
        // разрезания, поведение не меняем.
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
        var pageAssignments = new List<GostGroupingDocument>();
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
            pageAssignments.Add(new GostGroupingDocument(group.Code, displayName, [.. group.PageIndices]));
        }

        var documentsColumnPaths = baseColumnPaths.Concat(["КоличествоЛистов", "ФайлПуть", "РазмерБайт"]).ToArray();
        documents.UpdateCache(DataSetDtoMapper.SerializeSchema(BuildColumns(documentsColumnPaths, documentRows)), documentRows.Count, JsonSerializer.Serialize(documentRows));
        // Автораспознавание всегда перезаписывает предыдущую (в т.ч. ручную) группировку —
        // ManuallyEdited сбрасывается в false; предупреждение пользователю о потере ручных правок
        // показывает фронт ПЕРЕД вызовом этого метода (см. 409 Conflict в RecognizePdfSourceAsync
        // на уровне эндпоинта, когда ManuallyEdited уже true и confirm не передан).
        documents.SetGostGrouping(JsonSerializer.Serialize(new GostGroupingData(pageAssignments, ManuallyEdited: false)));

        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapSource(requestedSourceId == cover.Id ? cover : requestedSourceId == titlePage.Id ? titlePage : documents);
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "документ" : sanitized;
    }

    private static GostGroupingData? ParseGrouping(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<GostGroupingData>(json);

    // ФайлПуть каждой строки прежнего реестра "Документы" — CachedData: JSON-массив объектов
    // {..., "ФайлПуть": "...", ...} (та же форма, что пишет UpdateCache выше).
    private static HashSet<string> ExtractBlobPaths(string? cachedDataJson)
    {
        if (cachedDataJson is null) return [];
        var rows = JsonSerializer.Deserialize<List<Dictionary<string, string?>>>(cachedDataJson) ?? [];
        return rows.Select(r => r.GetValueOrDefault("ФайлПуть")).Where(p => !string.IsNullOrEmpty(p)).ToHashSet()!;
    }

    // ── Ручная корректировка разбиения ────────────────────────────────────────

    public async Task<GostGroupingDto?> GetPagesAsync(Guid sourceId, CancellationToken ct)
    {
        var source = await db.DataSetSources.Include(s => s.File).AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;
        if (source.SheetOrPath != PdfProfiles.GostDocumentsMarker)
            throw new ArgumentException("Ручная корректировка разбиения доступна только для источника «Документы» ГОСТ-профиля.");

        var pageCount = await GetPdfPageCountAsync(source.File.BlobPath, ct);
        var grouping = ParseGrouping(source.GostGrouping);
        var documents = grouping?.Documents.Select(d => new GostGroupingDocumentDto(d.Code, d.Name, d.PageIndices)).ToList()
            ?? [];
        return new GostGroupingDto(documents, grouping?.ManuallyEdited ?? false, pageCount);
    }

    public async Task<byte[]?> GetPageThumbnailAsync(Guid sourceId, int pageIndex, CancellationToken ct)
    {
        var source = await db.DataSetSources.Include(s => s.File).AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;
        if (source.SheetOrPath != PdfProfiles.GostDocumentsMarker)
            throw new ArgumentException("Миниатюры доступны только для источника «Документы» ГОСТ-профиля.");

        await using var stream = await blob.DownloadAsync(source.File.BlobPath, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        try
        {
            return await Task.Run(() => PdfRasterizer.ToPngPage(bytes, pageIndex), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ArgumentException($"Не удалось отрендерить страницу {pageIndex + 1}: {ex.Message}");
        }
    }

    public async Task<GostGroupingDto?> ApplyGroupingAsync(Guid sourceId, ApplyGroupingInput input, CancellationToken ct)
    {
        var source = await db.DataSetSources.Include(s => s.File).FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;
        if (source.SheetOrPath != PdfProfiles.GostDocumentsMarker)
            throw new ArgumentException("Ручная корректировка разбиения доступна только для источника «Документы» ГОСТ-профиля.");

        // Страница может не входить ни в одну группу (выпадает из реестра — допустимо), но
        // не может входить сразу в НЕСКОЛЬКО — иначе непонятно, какому документу она принадлежит.
        var seenPages = new HashSet<int>();
        foreach (var d in input.Documents)
            foreach (var p in d.PageIndices)
                if (!seenPages.Add(p))
                    throw new ArgumentException($"Страница {p + 1} назначена сразу нескольким документам.");

        // Осиротевшие blob'ы прежнего разбиения — удаляем best-effort после успешного пересчёта
        // (тот же паттерн, что и ReplaceFileAsync/DeleteFileAsync). Старые пути читаем из ещё не
        // перезаписанного CachedData — GostGrouping их не хранит (только индексы страниц).
        var previousBlobPaths = ExtractBlobPaths(source.CachedData);

        await using var stream = await blob.DownloadAsync(source.File.BlobPath, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        var baseColumnPaths = GostTitleBlockFields.All.Select(f => f.Path).ToArray();
        var documentsColumnPaths = baseColumnPaths.Concat(["КоличествоЛистов", "ФайлПуть", "РазмерБайт"]).ToArray();

        static DataSetColumnInfo[] BuildColumns(IReadOnlyList<string> columnPaths, IReadOnlyList<Dictionary<string, string?>> data) =>
            columnPaths.Select(p => new DataSetColumnInfo(p,
                data.Take(3).Select(r => r.GetValueOrDefault(p) ?? "").ToArray())).ToArray();

        var documentRows = new List<Dictionary<string, string?>>();
        var pageAssignments = new List<GostGroupingDocument>();
        foreach (var d in input.Documents.Where(d => d.PageIndices.Count > 0))
        {
            var row = new Dictionary<string, string?> { ["Шифр"] = d.Code, ["НаименованиеДокумента"] = d.Name };
            var pageIndices = d.PageIndices.OrderBy(i => i).ToList();
            try
            {
                var splitBytes = PdfPageSplitter.ExtractPages(bytes, pageIndices);
                var fileLabel = string.IsNullOrWhiteSpace(d.Name) ? d.Code : d.Name;
                var fileName = $"{SanitizeFileName(fileLabel)}.pdf";
                using var splitStream = new MemoryStream(splitBytes);
                row["ФайлПуть"] = await blob.UploadAsync(fileName, splitStream, "application/pdf", ct);
                row["РазмерБайт"] = splitBytes.Length.ToString();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Не удалось разрезать PDF при ручной корректировке для документа «{Code}» источника {SourceId}", d.Code, sourceId);
            }
            row["КоличествоЛистов"] = pageIndices.Count.ToString();
            documentRows.Add(row);
            pageAssignments.Add(new GostGroupingDocument(d.Code, d.Name, pageIndices));
        }

        source.UpdateCache(DataSetDtoMapper.SerializeSchema(BuildColumns(documentsColumnPaths, documentRows)), documentRows.Count, JsonSerializer.Serialize(documentRows));
        source.SetGostGrouping(JsonSerializer.Serialize(new GostGroupingData(pageAssignments, ManuallyEdited: true)));
        await db.SaveChangesAsync(ct);

        // Удаляем осиротевшие blob'ы прежнего разбиения ПОСЛЕ успешного сохранения новой
        // группировки — best-effort, недоступность/отсутствие старого файла не роняет операцию.
        var newBlobPaths = documentRows.Select(r => r.GetValueOrDefault("ФайлПуть")).Where(p => !string.IsNullOrEmpty(p)).ToHashSet();
        foreach (var oldPath in previousBlobPaths.Except(newBlobPaths!))
        {
            try { await blob.DeleteAsync(oldPath!, ct); }
            catch (Exception ex) { logger.LogWarning(ex, "Не удалось удалить осиротевший blob {BlobPath} при ручной корректировке источника {SourceId}", oldPath, sourceId); }
        }

        var pageCount = await GetPdfPageCountAsync(source.File.BlobPath, ct);
        return new GostGroupingDto(
            pageAssignments.Select(p => new GostGroupingDocumentDto(p.Code, p.Name, p.PageIndices)).ToList(),
            true, pageCount);
    }

    private async Task<int> GetPdfPageCountAsync(string blobPath, CancellationToken ct)
    {
        await using var stream = await blob.DownloadAsync(blobPath, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        using var doc = PdfDocument.Open(ms.ToArray());
        return doc.NumberOfPages;
    }
}
