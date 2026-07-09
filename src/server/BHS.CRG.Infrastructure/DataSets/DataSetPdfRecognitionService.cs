using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Notifications;
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
    INotificationService notifications,
    ILogger<DataSetPdfRecognitionService> logger
)
{
    // Комплект чертежей может быть большим (десятки листов) — выше, чем MaxPages=10 у
    // PdfRasterizer (тот подобран под сертификаты/декларации, не трогаем).
    private const int PdfRecognizeMaxPages = 100;

    /// <summary>Выбор профиля препроцессинга PDF-набора (issue #38). ГОСТ — набор-centric: ставит
    /// PreprocessingProfile на НАБОР, источников НЕ создаёт (их даёт распознавание как кандидатов),
    /// возвращает null. «Счёт на оплату» — осознанное source-centric исключение (нет кандидатной
    /// структуры): создаёт пару источников шапка+товары и возвращает шапку.</summary>
    public async Task<DataSetSourceDto?> CreatePdfSourceAsync(Guid fileId, CreatePdfSourceInput input, CancellationToken ct)
    {
        var file = await db.DataSetFiles.Include(f => f.Sources).FirstOrDefaultAsync(f => f.Id == fileId, ct)
            ?? throw new KeyNotFoundException($"DataSetFile {fileId} not found");
        if (file.Format != DataSetFormat.Pdf)
            throw new ArgumentException("Файл не в формате PDF.");

        var name = input.Name.Trim();
        bool HasMarker(string marker) => file.Sources.Any(s => s.SheetOrPath == marker);

        if (input.Profile == PdfProfiles.Invoice)
        {
            if (HasMarker(PdfProfiles.InvoiceHeaderMarker))
                throw new ArgumentException("На этом файле уже есть профиль «Счёт на оплату».");
            // Профиль "Счёт на оплату" — source-centric исключение: шапка + вложенная таблица товаров
            // как пара источников под одним файлом (у счёта нет кандидатной структуры страниц).
            var header = file.AddSource(name, PdfProfiles.InvoiceHeaderMarker, "[]", 0);
            var lineItems = file.AddSource($"{name} — Товары", PdfProfiles.InvoiceLineItemsMarker, "[]", 0);
            db.DataSetSources.Add(header);
            db.DataSetSources.Add(lineItems);
            file.SetPreprocessingProfile(PdfProfiles.Invoice);
            await db.SaveChangesAsync(ct);
            return DataSetDtoMapper.MapSource(header);
        }

        // Профиль "Основная надпись (ГОСТ Р 21.101-2020)" — набор-centric (issue #38): ставим профиль на
        // НАБОР, источников не создаём. Распознавание пишет Grouping (сырьё), обложка/титул/документы/
        // таблицы становятся кандидатами, пользователь создаёт источники из них.
        file.SetPreprocessingProfile(PdfProfiles.GostTitleBlock);
        await db.SaveChangesAsync(ct);
        return null;
    }

    // ── Набор-centric распознавание ГОСТ (issue #38): всё по fileId, источников не создаёт ──

    /// <summary>Планирование распознавания ГОСТ-набора по fileId (409-проверка ручной правки разбиения).</summary>
    public async Task<RecognizePlan?> PlanFileRecognitionAsync(Guid fileId, bool confirm, CancellationToken ct)
    {
        var file = await db.DataSetFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null) return null;
        if (file.Format != DataSetFormat.Pdf)
            throw new ArgumentException("Набор не в формате PDF.");
        var existingGrouping = ParseGrouping(file.Grouping);
        if (existingGrouping is { ManuallyEdited: true } && !confirm)
            throw new InvalidOperationException(
                "Разбиение набора было скорректировано вручную — повторное распознавание сотрёт ручные правки. Подтвердите, чтобы продолжить.");
        return new RecognizePlan(Background: true, Title: "Распознавание листов PDF");
    }

    /// <summary>Распознавание ГОСТ-комплекта по НАБОРУ: пишет Grouping (с вырезанными под-PDF), источников
    /// НЕ создаёт. Существующие источники-проекции переспроецируются. Кидает 409 при неподтверждённой
    /// ручной правке. Штатно идёт через фоновую задачу (Job.TargetId=fileId).</summary>
    public async Task RecognizeFileAsync(Guid fileId, bool confirm, CancellationToken ct, Func<int, int, Task>? onProgress = null)
    {
        var file = await db.DataSetFiles.FirstOrDefaultAsync(f => f.Id == fileId, ct)
            ?? throw new KeyNotFoundException($"DataSetFile {fileId} not found");
        if (file.Format != DataSetFormat.Pdf)
            throw new ArgumentException("Набор не в формате PDF.");
        var existingGrouping = ParseGrouping(file.Grouping);
        if (existingGrouping is { ManuallyEdited: true } && !confirm)
            throw new InvalidOperationException(
                "Разбиение набора было скорректировано вручную — повторное распознавание сотрёт ручные правки. Подтвердите, чтобы продолжить.");
        await RecognizeGostFileAsync(file, ct, onProgress);
    }

    public async Task<RecognizePlan?> PlanRecognitionAsync(Guid sourceId, bool confirm, CancellationToken ct)
    {
        var source = await db.DataSetSources.Include(s => s.File).AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source is null) return null;
        if (source.File.Format != DataSetFormat.Pdf)
            throw new ArgumentException("Источник не относится к PDF-файлу.");

        var isGost = source.SheetOrPath is PdfProfiles.GostCoverMarker or PdfProfiles.GostTitlePageMarker or PdfProfiles.GostDocumentsMarker;
        if (isGost)
        {
            // 409-проверка ручной правки — ДО постановки в фон (чтобы диалог подтверждения был интерактивным).
            // Группировка живёт на НАБОРЕ (issue #28), не на источнике.
            var existingGrouping = ParseGrouping(source.File.Grouping);
            if (existingGrouping is { ManuallyEdited: true } && !confirm)
                throw new InvalidOperationException(
                    "Разбиение этого источника было скорректировано вручную — повторное распознавание сотрёт ручные правки. Подтвердите, чтобы продолжить.");
            return new RecognizePlan(Background: true, Title: "Распознавание листов PDF");
        }
        // Счёт/legacy — короткие, синхронно.
        return new RecognizePlan(Background: false, Title: "Распознавание PDF");
    }

    public async Task<DataSetSourceDto?> RecognizePdfSourceAsync(Guid sourceId, bool confirm, CancellationToken ct,
        Func<int, int, Task>? onProgress = null)
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
            // Группировка живёт на НАБОРЕ (issue #28).
            var existingGrouping = ParseGrouping(source.File.Grouping);
            if (existingGrouping is { ManuallyEdited: true } && !confirm)
                throw new InvalidOperationException(
                    "Разбиение этого источника было скорректировано вручную — повторное распознавание сотрёт ручные правки. Подтвердите, чтобы продолжить.");

            // Мост для legacy source-centric вызова: делегируем в набор-centric распознавание (issue #38).
            // Новый штатный путь — RecognizeFileAsync(fileId); этот сохранён для существующих call-sites.
            await RecognizeGostFileAsync(source.File, ct, onProgress);
            return DataSetDtoMapper.MapSource(source);
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
    /// legacy-реестра, но с классификаторами ТипСтраницы/Форма (см. GostTitleBlockFields.AllWithClassifiers)
    /// и последующей маршрутизацией/группировкой (GostPageGrouper): обложка/титульный лист как
    /// есть, документы — сгруппированы по Шифру (не по НаименованиюДокумента — по ГОСТ Р
    /// 21.101-2020 форма 6, последующие листы и чертежей, и текстовых документов, обычно не
    /// повторяет наименование, но Шифр остаётся неизменным на всех листах документа) с
    /// разрезанием исходного PDF на под-файлы (PdfPageSplitter) для каждой группы.
    /// </summary>
    // Распознавание ГОСТ-комплекта на УРОВНЕ НАБОРА (issue #38): пишет только Grouping (с вырезанными
    // под-PDF в группах), источников НЕ создаёт. Существующие источники-проекции (обложка/титул/
    // документы/таблицы) переспроецируются из новой группировки. Кандидаты (см. PdfCandidatesAsync)
    // и создание источников — по запросу пользователя.
    private async Task RecognizeGostFileAsync(DataSetFile file, CancellationToken ct,
        Func<int, int, Task>? onProgress = null)
    {
        await using var stream = await blob.DownloadAsync(file.BlobPath, ct);
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

        // Постраничная проверка текстового слоя (бесплатно, PdfPig) — гейт для второго прохода
        // распознавания: страницы форма 3 (чертёж) обычно НЕ имеют текстового слоя (CAD-экспорт
        // рисует штамп как графику) и распознаются заметно менее надёжно, чем форма 5/6 с
        // текстовым слоем — там, где текста нет, точность реально страдает, добавляем второй
        // проход на обрезанном штампе в высоком эффективном разрешении. Там, где текст есть,
        // распознавание уже надёжно — второй проход не даёт выигрыша, не делаем его (не удваиваем
        // стоимость без оснований). См. память проекта project_pdf_gost_split_documents.md.
        // По умолчанию — "текстовый слой есть" (второй проход НЕ включается), пока PdfPig не
        // скажет обратное для конкретной страницы; если разбор целиком не удался (catch ниже),
        // весь массив остаётся в этом безопасном состоянии — второй проход отключён везде.
        var pageHasTextLayer = Enumerable.Repeat(true, pngPages.Count).ToArray();
        var pageSizes = new System.Drawing.SizeF[pngPages.Count];
        // Точный текст области штампа (текстовый слой + аннотации) на каждую страницу — «опора»
        // для распознавания. Пусто там, где штамп чисто графический (ни слоя, ни аннотаций).
        var pageStampText = new IReadOnlyList<string>[pngPages.Count];
        Array.Fill(pageStampText, Array.Empty<string>());
        try
        {
            using var pdfDoc = PdfDocument.Open(bytes);
            foreach (var pdfPage in pdfDoc.GetPages())
            {
                var i = pdfPage.Number - 1;
                if (i < 0 || i >= pageHasTextLayer.Length) continue;
                pageHasTextLayer[i] = pdfPage.Letters.Count > 0;
                // PdfPig.Width/Height уже учитывают поворот (/Rotate) — та же "визуальная"
                // система координат, что ожидает PDFtoImage.RenderOptions.Bounds (подтверждено
                // экспериментально, см. GostTitleBlockRegion).
                pageSizes[i] = new System.Drawing.SizeF((float)pdfPage.Width, (float)pdfPage.Height);
                // Регион по форме 3 (наибольший) — накрывает штамп любой формы; форму на этом
                // этапе ещё не знаем, но для извлечения текста это и не нужно.
                var stampRegion = GostTitleBlockRegion.ComputeBottomRightRegion(pageSizes[i].Width, pageSizes[i].Height);
                pageStampText[i] = GostStampTextExtractor.Extract(pdfPage, stampRegion);
            }
        }
        catch (Exception ex)
        {
            // Не удалось разобрать PDF через PdfPig — считаем, что текстовый слой есть везде
            // (второй проход не включаем нигде); растеризация через PdfRasterizer уже сработала
            // выше, так что это НЕ повод падать всей операции.
            logger.LogWarning(ex, "Не удалось проверить текстовый слой PDF источника {SourceId} — второй проход штампа отключён", file.Id);
        }

        var fields = GostTitleBlockFields.AllWithClassifiers;
        var stampFields = GostTitleBlockFields.All;
        var rows = new List<IReadOnlyDictionary<string, string?>>();
        var failedPages = 0; // листы, распознавание которых не удалось (строка осталась пустой) — для уведомления
        for (var i = 0; i < pngPages.Count; i++)
        {
            if (onProgress is not null) await onProgress(i + 1, pngPages.Count); // честный прогресс для индикатора

            Dictionary<string, string?> values;
            // Если в PDF есть точный текст штампа — отдаём его модели как «опору» (grounding),
            // чтобы она не «исправляла» точный шифр/имя по мутной картинке.
            var stampText = pageStampText[i];
            Func<IReadOnlyList<RecognitionField>, string> promptBuilder = stampText.Count > 0
                ? f => RecognitionShared.BuildTitleBlockPromptWithGrounding(f, stampText)
                : RecognitionShared.BuildTitleBlockPrompt;
            try
            {
                var result = await recognizer.RecognizeAsync(
                    pngPages[i], "image/png", fields, promptBuilder, ct: ct);
                values = new Dictionary<string, string?>(result.Values);
            }
            catch (Exception ex) when (ex is RecognitionUnavailableException or RecognitionLimitException)
            {
                if (i == 0)
                    throw new ArgumentException($"Распознавание недоступно: {ex.Message}");
                logger.LogWarning(ex, "Распознавание страницы {Page} источника {SourceId} не удалось — строка останется пустой", i + 1, file.Id);
                rows.Add(fields.ToDictionary(f => f.Path, string? (f) => null));
                failedPages++;
                continue;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Таймаут vision-движка на ОДНОЙ странице (HttpClient.Timeout истёк, ct задачи НЕ отменён) —
                // не роняем весь альбом: строка остаётся пустой, пользователь при желании перераспознает этот
                // документ точечно («Перераспознать»). Та же философия отказоустойчивости по странице.
                logger.LogWarning("Таймаут распознавания страницы {Page} источника {SourceId} — строка останется пустой", i + 1, file.Id);
                rows.Add(fields.ToDictionary(f => f.Path, string? (f) => null));
                failedPages++;
                continue;
            }

            // Второй проход (vision-спасение по укрупнённому кропу штампа) запускаем, если: (а) у
            // страницы нет текстового слоя (штамп нарисован графикой — на полной странице читается
            // хуже); ИЛИ (б) наименование документа не распозналось в пасс-1 на листе-документе.
            // НО только когда точного текста штампа НЕТ вовсе (ни слоя, ни аннотаций): если он есть,
            // грундованный пасс-1 надёжнее кроп-OCR, и лишний vision-проход мог бы затереть точные
            // значения (см. GostStampPassMerge — пасс-2 приоритетен). Обложку/титул не спасаем.
            var pageType = values.GetValueOrDefault(GostTitleBlockFields.PageTypePath);

            // Обложка/титульный лист: штампа с шифром на них нет — графы штампа (пасс-1) пусты.
            // Распознаём ОТДЕЛЬНЫМ набором полей заглавного листа (GostCoverTitleFields) по всему
            // листу, сохраняя классификатор ТипСтраницы для маршрутизации. Стамп-кроп пасс-2 им не нужен.
            if (pageType is "Обложка" or "ТитульныйЛист")
            {
                try
                {
                    var coverResult = await recognizer.RecognizeAsync(
                        pngPages[i], "image/png", GostCoverTitleFields.All, RecognitionShared.BuildCoverTitlePrompt, ct: ct);
                    values = new Dictionary<string, string?>(coverResult.Values)
                    {
                        [GostTitleBlockFields.PageTypePath] = pageType,
                    };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Распознавание заглавного листа (обложка/титул) стр. {Page} источника {SourceId} не удалось — поля останутся пустыми", i + 1, file.Id);
                }
                rows.Add(values);
                continue;
            }

            // Второй проход (vision-спасение по укрупнённому кропу штампа) — только для листов-документов.
            var nameMissingOnDocument = string.IsNullOrWhiteSpace(values.GetValueOrDefault("НаименованиеДокумента"));
            if (stampText.Count == 0 && (!pageHasTextLayer[i] || nameMissingOnDocument))
            {
                try
                {
                    var pageSize = pageSizes[i];
                    var stampForm = values.GetValueOrDefault(GostTitleBlockFields.StampFormPath);
                    var region = GostTitleBlockRegion.ComputeBottomRightRegion(pageSize.Width, pageSize.Height, stampForm);
                    var cropPng = await Task.Run(() => PdfRasterizer.ToPngRegion(bytes, i, region), ct);
                    var cropResult = await recognizer.RecognizeAsync(
                        cropPng, "image/png", stampFields, RecognitionShared.BuildTitleBlockPrompt, ct: ct);

                    // Пасс-2 (кроп штампа в высоком разрешении) всегда приоритетен для прочитанных им
                    // полей — объединяем поверх пасс-1. Классификаторы ТипСтраницы/Форма во втором
                    // проходе не запрашиваются (stampFields), поэтому автоматически остаются из пасс-1.
                    values = GostStampPassMerge.Merge(values, cropResult.Values);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Второй проход не обязателен — при любой ошибке (растеризация региона,
                    // недоступность распознавателя) просто остаёмся на результате первого прохода.
                    logger.LogWarning(ex, "Второй проход (штамп в высоком разрешении) для страницы {Page} источника {SourceId} не удался — используется результат обычного распознавания", i + 1, file.Id);
                }
            }

            rows.Add(values);
        }

        // Единая постраничная группировка (обложка/титул/документы как группы) — источник истины СЫРЬЯ
        // набора. Одна точка агрегации полей. Стабильные id групп (issue #28) переносим из предыдущей
        // группировки НАБОРА при перераспознавании, чтобы производные источники (gost-table:{id},
        // проекции) не осиротели.
        var routed = GostPageGrouper.Group(rows);
        var existingGrouping = ParseGrouping(file.Grouping);
        var unified = GostStableIds.Assign(GostUnifiedGroupingBuilder.Build(routed, rows, manuallyEdited: false), existingGrouping);

        // Материализация СЫРЬЯ на наборе: режем под-PDF в группы (BlobPath в Grouping), пишем Grouping,
        // переспроецируем существующие источники-проекции, чистим осиротевшие блобы. Источников НЕ создаём.
        var matResult = await MaterializeFileGroupingAsync(file, unified, bytes, ct);
        await PublishGostRecognitionResultAsync(matResult.DocumentCount, rows.Count, failedPages, matResult.FailedSplits, matResult.InvalidatedTables, ct);
    }

    private record GostMaterializeResult(int DocumentCount, int FailedSplits, int InvalidatedTables);

    // Материализация СЫРЬЯ ГОСТ-набора (issue #38, набор-centric): режет под-PDF каждой группы-документа
    // в BlobPath группы (внутри Grouping), пишет Grouping на набор, переспроецирует СУЩЕСТВУЮЩИЕ источники-
    // проекции (обложка/титул/документы/таблицы) из новой группировки, чистит осиротевшие блобы.
    // Источников НЕ создаёт — они кандидаты, создаются пользователем. Общая точка для автораспознавания
    // и ручной правки разбиения (ApplyGrouping).
    private async Task<GostMaterializeResult> MaterializeFileGroupingAsync(
        DataSetFile file, GostGroupingData unified, byte[] bytes, CancellationToken ct)
    {
        var previousBlobs = ExtractGroupBlobPaths(ParseGrouping(file.Grouping));
        var withBlobs = await SplitDocumentsIntoGroupingAsync(bytes, unified, file.Id, ct);
        file.SetGrouping(JsonSerializer.Serialize(withBlobs));

        var projected = GostGroupingProjection.Project(withBlobs);
        await RefreshProjectionSourcesAsync(file.Id, projected, ct);
        var invalidatedTables = await ReprojectTableSourcesAsync(file.Id, withBlobs, ct);

        await db.SaveChangesAsync(ct);

        var newBlobs = withBlobs.Groups.Select(g => g.BlobPath).Where(p => !string.IsNullOrEmpty(p)).ToHashSet()!;
        await DeleteOrphanGroupBlobsAsync(previousBlobs, newBlobs, file.Id, ct);

        var failedSplits = withBlobs.Groups.Count(g => g.Kind == GostGroupKind.Document && g.Pages.Count > 0 && string.IsNullOrEmpty(g.BlobPath));
        return new GostMaterializeResult(projected.Documents.Count, failedSplits, invalidatedTables);
    }

    // Режет под-PDF каждой группы-документа и возвращает НОВУЮ группировку с BlobPath/BlobSize в группах
    // (сырьё живёт в Grouping). Отказоустойчиво: сбой одной группы оставляет её без блоба, не роняя остальные.
    private async Task<GostGroupingData> SplitDocumentsIntoGroupingAsync(
        byte[] bytes, GostGroupingData unified, Guid fileIdForLog, CancellationToken ct)
    {
        var groups = new List<GostGroupingGroup>(unified.Groups.Count);
        foreach (var g in unified.Groups)
        {
            if (g.Kind != GostGroupKind.Document || g.Pages.Count == 0) { groups.Add(g with { BlobPath = null, BlobSize = null }); continue; }
            var label = !string.IsNullOrWhiteSpace(g.Name) ? g.Name! : (g.Code ?? "документ");
            try
            {
                var pageIndices = g.Pages.Select(p => p.PageIndex).ToList();
                var splitBytes = PdfPageSplitter.ExtractPages(bytes, pageIndices);
                using var splitStream = new MemoryStream(splitBytes);
                var blobPath = await blob.UploadAsync($"{SanitizeFileName(label)}.pdf", splitStream, "application/pdf", ct);
                groups.Add(g with { BlobPath = blobPath, BlobSize = splitBytes.Length });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Не удалось разрезать PDF для документа «{DocumentLabel}» набора {FileId}", label, fileIdForLog);
                groups.Add(g with { BlobPath = null, BlobSize = null });
            }
        }
        return unified with { Groups = groups };
    }

    // Переспроецирует СУЩЕСТВУЮЩИЕ источники-проекции набора (обложка/титул/документы) из новой группировки.
    // Таблицы — отдельно (ReprojectTableSourcesAsync). Источники, которых нет — не создаёт (кандидаты).
    private async Task RefreshProjectionSourcesAsync(Guid fileId, ProjectedRows projected, CancellationToken ct)
    {
        var coverColumnPaths = GostCoverTitleFields.All.Select(f => f.Path).ToArray();
        var documentsColumnPaths = GostTitleBlockFields.All.Select(f => f.Path)
            .Concat(["КоличествоЛистов", "ФайлПуть", "РазмерБайт"]).ToArray();

        static DataSetColumnInfo[] Cols(IReadOnlyList<string> paths, IReadOnlyList<IReadOnlyDictionary<string, string?>> data) =>
            paths.Select(p => new DataSetColumnInfo(p, data.Take(3).Select(r => r.TryGetValue(p, out var v) ? v ?? "" : "").ToArray())).ToArray();

        var sources = await db.DataSetSources.Where(s => s.FileId == fileId).ToListAsync(ct);
        var cover = sources.FirstOrDefault(s => s.SheetOrPath == PdfProfiles.GostCoverMarker);
        var title = sources.FirstOrDefault(s => s.SheetOrPath == PdfProfiles.GostTitlePageMarker);
        var documents = sources.FirstOrDefault(s => s.SheetOrPath == PdfProfiles.GostDocumentsMarker);

        cover?.UpdateCache(DataSetDtoMapper.SerializeSchema(Cols(coverColumnPaths, projected.Cover)), projected.Cover.Count, JsonSerializer.Serialize(projected.Cover));
        title?.UpdateCache(DataSetDtoMapper.SerializeSchema(Cols(coverColumnPaths, projected.TitlePage)), projected.TitlePage.Count, JsonSerializer.Serialize(projected.TitlePage));
        if (documents is not null)
        {
            var docRows = projected.Documents.Select(d => (IReadOnlyDictionary<string, string?>)d.Fields).ToList();
            documents.UpdateCache(DataSetDtoMapper.SerializeSchema(Cols(documentsColumnPaths, docRows)), docRows.Count, JsonSerializer.Serialize(docRows));
        }
    }

    private static HashSet<string> ExtractGroupBlobPaths(GostGroupingData? grouping) =>
        grouping is null ? [] : grouping.Groups.Select(g => g.BlobPath).Where(p => !string.IsNullOrEmpty(p)).ToHashSet()!;

    private async Task DeleteOrphanGroupBlobsAsync(HashSet<string> previous, HashSet<string> current, Guid fileIdForLog, CancellationToken ct)
    {
        foreach (var oldPath in previous.Except(current))
        {
            try { await blob.DeleteAsync(oldPath, ct); }
            catch (Exception ex) { logger.LogWarning(ex, "Не удалось удалить осиротевший blob {BlobPath} набора {FileId}", oldPath, fileIdForLog); }
        }
    }

    // Итоговое уведомление о распознавании групп листов PDF: Info при чистом успехе, Warning при
    // частичных сбоях (нераспознанные листы / документы без файла / инвалидированные табличные
    // источники — последнее требует ручной перепроверки). Системное (userId=null): эндпоинт
    // распознавания не несёт контекст пользователя, действие админ-конфигурационное.
    private async Task PublishGostRecognitionResultAsync(
        int documentCount, int pageCount, int failedPages, int failedSplits, int invalidatedTables, CancellationToken ct)
    {
        var msg = $"Распознано: {documentCount} документов, {pageCount} листов.";
        if (failedPages > 0) msg += $" Не распозналось листов: {failedPages}.";
        if (failedSplits > 0) msg += $" Документов без файла: {failedSplits}.";
        if (invalidatedTables > 0)
            msg += $" Табличные источники ({invalidatedTables}) инвалидированы — границы документов изменились," +
                   " проверьте/перераспознайте их вручную.";

        var hasIssues = failedPages > 0 || failedSplits > 0 || invalidatedTables > 0;
        await notifications.PublishAsync(
            hasIssues ? NotificationSeverity.Warning : NotificationSeverity.Info,
            "Распознавание групп листов PDF завершено", msg, "Распознавание PDF", ct: ct);
    }

    // Тип поля схемы → тип поля распознавания (консервативно: число/дата, остальное — строка).
    private static string MapRecognitionType(string schemaType) => schemaType switch
    {
        "number" => "number",
        "date" => "date",
        _ => "string",
    };

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "документ" : sanitized;
    }

    /// <summary>Читает единую группировку, ТОЛЕРАНТНО к старому формату (до фазы «обложка/титул как
    /// группы»): старый JSON вида {Documents:[{Code,Name,PageIndices}]} без Kind/полей страниц
    /// маппится в группы Kind=Document с пустыми полями страниц (перераспознавание восстановит поля).</summary>
    // Толерантный разбор группировки вынесен в тестируемый GostGroupingSerialization; тонкий
    // делегат сохраняет прежние call-sites внутри сервиса.
    private static GostGroupingData? ParseGrouping(string? json)
    {
        var data = GostGroupingSerialization.Parse(json);
        // Гарантируем стабильные id (issue #28): свежая группировка их уже несёт (GostStableIds.Assign);
        // это подстраховка для группировок, прочитанных без id.
        return data is null ? null : GostStableIds.EnsureIds(data);
    }

    // gost-table:* — детерминированная ПРОЕКЦИЯ группы-документа, не независимый источник. При ре-
    // группировке/ре-распознавании границы документов смещаются: табличный источник остаётся валидным,
    // только если его каноническая первая страница ВСЁ ЕЩЁ начинает документ, помеченный табличным тэгом
    // (тогда его CachedData ещё соответствует границам, ключ не меняется). Иначе источник осиротел —
    // распознан для больше-не-существующих границ — и удаляется (P1b/c). Привязки к нему каскадно
    // удалятся (FK Cascade) — если они были, это ломает маппинг, поэтому предупреждаем в лог.
    // Вызывать ДО SaveChangesAsync (в той же транзакции, что и запись новой группировки).
    // Возвращает число удалённых (осиротевших) табличных источников — для предупреждения в уведомлении.
    private async Task<int> ReprojectTableSourcesAsync(Guid fileId, GostGroupingData unified, CancellationToken ct)
    {
        var allSources = await db.DataSetSources.Where(s => s.FileId == fileId).ToListAsync(ct);
        var tableSources = allSources
            .Where(s => s.SheetOrPath.StartsWith(PdfProfiles.GostTableMarkerPrefix, StringComparison.Ordinal))
            .ToList();
        if (tableSources.Count == 0) return 0;

        // Стабильные id текущих документов, всё ещё помеченных табличным тэгом (issue #28).
        var validGroupIds = unified.Groups
            .Where(g => g.Kind == GostGroupKind.Document && g.Pages.Count > 0
                        && (g.Tags ?? []).Any(t => GostTableFields.ColumnsForTag(t) is not null))
            .Select(g => g.Id)
            .ToHashSet();

        var removed = 0;
        foreach (var ts in tableSources)
        {
            var idStr = ts.SheetOrPath[PdfProfiles.GostTableMarkerPrefix.Length..];
            if (Guid.TryParse(idStr, out var gid) && validGroupIds.Contains(gid))
                continue; // документ сохранился (id совпал) — оставляем табличный источник как есть

            var boundCount = await db.DataSetBindings.CountAsync(b => b.SourceId == ts.Id, ct);
            if (boundCount > 0)
                logger.LogWarning("Удаляю осиротевший табличный источник {SourceId} ({Marker}) файла {FileId} — границы документа изменились; на него ссылались {BindingCount} привязок, они перестанут работать",
                    ts.Id, ts.SheetOrPath, fileId, boundCount);
            else
                logger.LogInformation("Удаляю осиротевший табличный источник {SourceId} ({Marker}) файла {FileId} — границы документа изменились",
                    ts.Id, ts.SheetOrPath, fileId);
            db.DataSetSources.Remove(ts);
            removed++;
        }
        return removed;
    }

    // ── Редактор разбиения — на уровне НАБОРА (issue #38, fileId) ──────────────────

    public async Task<GostGroupingDto?> GetPagesAsync(Guid fileId, CancellationToken ct)
    {
        var file = await db.DataSetFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file == null) return null;
        if (file.Format != DataSetFormat.Pdf)
            throw new ArgumentException("Разбиение доступно только для PDF-набора.");

        var pageCount = await GetPdfPageCountAsync(file.BlobPath, ct);
        var grouping = ParseGrouping(file.Grouping);
        var groups = (grouping?.Groups ?? [])
            .Select(g => new GostGroupingGroupDto(g.Kind, g.Code, g.Name, g.Pages.Select(p => p.PageIndex).ToList(), g.Tags))
            .ToList();
        return new GostGroupingDto(groups, grouping?.ManuallyEdited ?? false, pageCount);
    }

    public async Task<byte[]?> GetPageThumbnailAsync(Guid fileId, int pageIndex, CancellationToken ct, int dpi = 96)
    {
        var file = await db.DataSetFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file == null) return null;
        if (file.Format != DataSetFormat.Pdf)
            throw new ArgumentException("Миниатюры доступны только для PDF-набора.");

        await using var stream = await blob.DownloadAsync(file.BlobPath, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        try
        {
            return await Task.Run(() => PdfRasterizer.ToPngPage(bytes, pageIndex, dpi), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ArgumentException($"Не удалось отрендерить страницу {pageIndex + 1}: {ex.Message}");
        }
    }

    /// <summary>
    /// Распознаёт таблицу помеченного документа (тэг спецификации/кабельного журнала) и заводит её
    /// как отдельный табличный DataSet-источник (маркер <c>gost-table:{перваяСтраница}</c>) — он
    /// появляется в списке источников файла и выгружается общим экспортом (ExportSourceAsync).
    /// Распознавание — по образцу таблицы товаров счёта: под-PDF документа одним vision-вызовом,
    /// фиксированные колонки под тип (GostTableFields). Источник создаётся/обновляется идемпотентно.
    /// </summary>
    /// <summary>Лёгкая установка функциональных тэгов документа (тип таблицы) в единой группировке —
    /// без пересборки/разрезания PDF и без сброса ManuallyEdited (в отличие от ApplyGroupingAsync).
    /// firstPageIndex — любая страница документа.</summary>
    public async Task<GostGroupingDto?> SetDocumentTagsAsync(Guid fileId, int firstPageIndex, IReadOnlyList<string> tags, CancellationToken ct)
    {
        var file = await db.DataSetFiles.FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file == null) return null;
        if (file.Format != DataSetFormat.Pdf)
            throw new ArgumentException("Тэги документа доступны только для PDF-набора.");

        var grouping = ParseGrouping(file.Grouping);
        if (grouping is null)
            throw new ArgumentException("Группировка ещё не распознана.");
        // Оставляем только известные тэги типа таблицы (не даём проставить произвольные).
        var clean = tags.Where(t => GostTableFields.ColumnsForTag(t) is not null).Distinct().ToList();
        var updated = grouping.Groups
            .Select(g => g.Kind == GostGroupKind.Document && g.Pages.Any(p => p.PageIndex == firstPageIndex)
                ? g with { Tags = clean.Count > 0 ? clean : null }
                : g)
            .ToList();
        file.SetGrouping(JsonSerializer.Serialize(new GostGroupingData(updated, grouping.ManuallyEdited)));
        await db.SaveChangesAsync(ct);

        var pageCount = await GetPdfPageCountAsync(file.BlobPath, ct);
        return new GostGroupingDto(
            updated.Select(g => new GostGroupingGroupDto(g.Kind, g.Code, g.Name, g.Pages.Select(p => p.PageIndex).ToList(), g.Tags)).ToList(),
            grouping.ManuallyEdited, pageCount);
    }

    public async Task<DataSetSourceDto?> RecognizeDocumentTableAsync(Guid fileId, int firstPageIndex, CancellationToken ct)
    {
        var file = await db.DataSetFiles.Include(f => f.Sources).FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file == null) return null;
        if (file.Format != DataSetFormat.Pdf)
            throw new ArgumentException("Распознавание таблицы доступно только для PDF-набора.");

        var grouping = ParseGrouping(file.Grouping);
        var group = grouping?.Groups.FirstOrDefault(
            g => g.Kind == GostGroupKind.Document && g.Pages.Any(p => p.PageIndex == firstPageIndex));
        if (group is null)
            throw new ArgumentException("Документ с указанной страницей не найден в группировке.");

        // Тэг таблицы документа → целевой ТИП, объявивший этот тэг (issue #29, «тип объявляет тэг»):
        // таблица распознаётся в поля типа и материализуется в него (#19). Fallback — легаси
        // хардкод-колонки GostTableFields, пока целевой тип не объявлен (переходный период).
        var tag = (group.Tags ?? []).FirstOrDefault(t => GostTableFields.ColumnsForTag(t) is not null);
        if (tag is null)
            throw new ArgumentException("У документа не задан тип таблицы (спецификация/кабельный журнал).");

        var allTypes = await db.DocumentTypes.AsNoTracking().ToListAsync(ct);
        var targetType = allTypes.FirstOrDefault(t => SchemaTags.TypeHasTag(t, allTypes, tag));

        IReadOnlyList<RecognitionField> columns;
        List<SchemaFieldInfo> typeFields = [];
        if (targetType is not null)
        {
            var typesById = allTypes.ToDictionary(t => t.Id);
            typeFields = DocumentTypeSchemaReader.EffectiveFields(targetType.Id, typesById)
                .Where(f => SchemaFieldKinds.IsScalar(f.Type))
                .ToList();
            if (typeFields.Count == 0)
                throw new ArgumentException($"У типа «{targetType.Name}» нет скалярных полей для распознавания таблицы.");
            columns = typeFields
                .Select(f => new RecognitionField(f.Key, f.Title ?? f.Key, MapRecognitionType(f.Type)))
                .ToList();
        }
        else
        {
            columns = GostTableFields.ColumnsForTag(tag)!;
        }

        await using var stream = await blob.DownloadAsync(file.BlobPath, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        var pageIndices = group.Pages.Select(p => p.PageIndex).OrderBy(i => i).ToList();
        byte[] subPdf;
        try { subPdf = PdfPageSplitter.ExtractPages(bytes, pageIndices); }
        catch (Exception ex) { throw new ArgumentException($"Не удалось выделить страницы документа: {ex.Message}"); }

        RecognitionResult result;
        try
        {
            result = await recognizer.RecognizeAsync(subPdf, "application/pdf",
                GostTableFields.RecognitionFieldsFor(columns), RecognitionShared.BuildTablePrompt, ct: ct);
        }
        catch (Exception ex) when (ex is RecognitionUnavailableException or RecognitionLimitException)
        {
            throw new ArgumentException($"Распознавание недоступно: {ex.Message}");
        }

        var rows = GostTableFields.SplitRows(result.Values, columns);
        var schema = columns
            .Select(c => new DataSetColumnInfo(c.Path, rows.Take(3).Select(r => r.GetValueOrDefault(c.Path) ?? "").ToArray()))
            .ToArray();
        var schemaJson = DataSetDtoMapper.SerializeSchema(schema);
        var dataJson = JsonSerializer.Serialize(rows);
        // Ключ и имя — по КАНОНИЧЕСКОЙ первой странице документа (минимум страниц группы), а не по
        // входному firstPageIndex: иначе два вызова с разными страницами одного документа создали бы
        // два источника-дубля. firstPageIndex — лишь «указатель на документ», не идентичность таблицы.
        var canonicalFirstPage = pageIndices[0];
        var sourceName = string.IsNullOrWhiteSpace(group.Name) ? $"Таблица (стр. {canonicalFirstPage + 1})" : group.Name!;

        // Идемпотентно: один табличный источник на документ. Ключ — СТАБИЛЬНЫЙ id группы (issue #28),
        // а не firstPageIndex: переживает перераспознавание/сдвиг страниц — источник не осиротеет (P1).
        var marker = $"{PdfProfiles.GostTableMarkerPrefix}{group.Id}";
        var tableSource = file.Sources.FirstOrDefault(s => s.SheetOrPath == marker);
        if (tableSource is null)
        {
            tableSource = file.AddSource(sourceName, marker, schemaJson, rows.Count);
            // Тип таблицы (спецификация/кабельный журнал) НЕ дублируем в DataSetSource.Tags: тэг живёт
            // на группе-документе (GostGrouping, scope GostDocument), а тип источника уже неявно задан
            // фиксированными колонками GostTableFields. Ранее сюда клался документный тэг в поле под
            // Dataset-scope тэги источника — семантическое смешение, ничего его не читало (P7).
            db.DataSetSources.Add(tableSource);
        }
        tableSource.UpdateCache(schemaJson, rows.Count, dataJson);
        // Сходимость с #19 (issue #29): тэг разрешился в тип → источник-таблица материализуется в него.
        // Маппинг идентичный (ключ→колонка) — распознавали прямо в ключи полей типа.
        if (targetType is not null)
            tableSource.SetMaterialization(targetType.Id, JsonSerializer.Serialize(typeFields.ToDictionary(f => f.Key, f => f.Key)));
        await db.SaveChangesAsync(ct);
        await notifications.PublishAsync(NotificationSeverity.Info, "Таблица распознана",
            $"«{sourceName}» — строк: {rows.Count}. Доступна как отдельный источник (выгрузка XLSX/CSV).", "Распознавание PDF", ct: ct);
        return DataSetDtoMapper.MapSource(tableSource);
    }

    /// <summary>Точечное перераспознавание ОДНОГО документа (P6): заново распознаёт только страницы его
    /// группы (vision — дорого, но лишь по этим листам, не по всему альбому), обновляет их поля/шифр/
    /// наименование в единой группировке, СОХРАНЯЯ структуру (границы страниц) и тэги документа. Прочие
    /// группы (обложка/титул/другие документы) не трогаются. Дальше — общий хвост материализации
    /// (проекция → разрезание → каши источников → инвалидация табличных → cleanup), как у ApplyGrouping.</summary>
    public async Task<GostGroupingDto?> RecognizeDocumentAsync(Guid fileId, int firstPageIndex, CancellationToken ct,
        Func<int, int, Task>? onProgress = null)
    {
        var file = await db.DataSetFiles.FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null) return null;
        if (file.Format != DataSetFormat.Pdf)
            throw new ArgumentException("Перераспознавание документа доступно только для PDF-набора.");

        var grouping = ParseGrouping(file.Grouping);
        var groups = grouping?.Groups.ToList();
        var targetIdx = groups?.FindIndex(g => g.Kind == GostGroupKind.Document && g.Pages.Any(p => p.PageIndex == firstPageIndex)) ?? -1;
        if (grouping is null || groups is null || targetIdx < 0)
            throw new ArgumentException("Документ с указанной страницей не найден в группировке.");
        var target = groups[targetIdx];

        await using var stream = await blob.DownloadAsync(file.BlobPath, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        // Размеры/текст штампа/наличие текстового слоя целевых страниц (как в RecognizeGostSetAsync, но
        // только по страницам документа).
        var pageSizes = new Dictionary<int, System.Drawing.SizeF>();
        var pageStampText = new Dictionary<int, IReadOnlyList<string>>();
        var pageHasTextLayer = new Dictionary<int, bool>();
        try
        {
            using var pdfDoc = PdfDocument.Open(bytes);
            foreach (var p in target.Pages)
            {
                var idx = p.PageIndex;
                if (idx < 0 || idx >= pdfDoc.NumberOfPages) continue;
                var page = pdfDoc.GetPage(idx + 1);
                pageHasTextLayer[idx] = page.Letters.Count > 0;
                var size = new System.Drawing.SizeF((float)page.Width, (float)page.Height);
                pageSizes[idx] = size;
                pageStampText[idx] = GostStampTextExtractor.Extract(page, GostTitleBlockRegion.ComputeBottomRightRegion(size.Width, size.Height));
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Не удалось проверить текстовый слой при перераспознавании документа {FileId}", file.Id); }

        // Перераспознаём страницы документа (пасс-1 grounding + пасс-2 кроп штампа — тот же путь, что для
        // листов-документов в RecognizeGostSetAsync).
        var fields = GostTitleBlockFields.AllWithClassifiers;
        var freshRows = new Dictionary<int, IReadOnlyDictionary<string, string?>>();
        var done = 0;
        foreach (var p in target.Pages)
        {
            var idx = p.PageIndex;
            if (onProgress is not null) await onProgress(++done, target.Pages.Count);
            var stampText = pageStampText.GetValueOrDefault(idx, Array.Empty<string>());
            Func<IReadOnlyList<RecognitionField>, string> promptBuilder = stampText.Count > 0
                ? f => RecognitionShared.BuildTitleBlockPromptWithGrounding(f, stampText)
                : RecognitionShared.BuildTitleBlockPrompt;
            Dictionary<string, string?> values;
            try
            {
                var png = await Task.Run(() => PdfRasterizer.ToPngPage(bytes, idx, PdfRasterizer.DefaultDpi), ct);
                var result = await recognizer.RecognizeAsync(png, "image/png", fields, promptBuilder, ct: ct);
                values = new Dictionary<string, string?>(result.Values);
            }
            catch (Exception ex) when (ex is RecognitionUnavailableException or RecognitionLimitException)
            {
                throw new ArgumentException($"Распознавание недоступно: {ex.Message}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Таймаут vision-движка на странице — оставляем её поля пустыми, не роняя перераспознавание.
                logger.LogWarning("Таймаут распознавания стр. {Page} при перераспознавании документа {FileId}", idx + 1, file.Id);
                values = fields.ToDictionary(f => f.Path, string? (f) => null);
            }

            var nameMissing = string.IsNullOrWhiteSpace(values.GetValueOrDefault("НаименованиеДокумента"));
            var size = pageSizes.GetValueOrDefault(idx);
            if (stampText.Count == 0 && size.Width > 0 && (!pageHasTextLayer.GetValueOrDefault(idx, true) || nameMissing))
            {
                try
                {
                    var form = values.GetValueOrDefault(GostTitleBlockFields.StampFormPath);
                    var region = GostTitleBlockRegion.ComputeBottomRightRegion(size.Width, size.Height, form);
                    var cropPng = await Task.Run(() => PdfRasterizer.ToPngRegion(bytes, idx, region), ct);
                    var cropResult = await recognizer.RecognizeAsync(cropPng, "image/png", GostTitleBlockFields.All, RecognitionShared.BuildTitleBlockPrompt, ct: ct);
                    values = GostStampPassMerge.Merge(values, cropResult.Values);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Второй проход штампа при перераспознавании стр. {Page} набора {FileId} не удался", idx + 1, file.Id);
                }
            }
            freshRows[idx] = values;
        }

        // Пересобираем ТОЛЬКО целевую группу: свежие поля страниц, шифр/имя из свежего распознавания;
        // границы страниц и тэги документа сохраняем (тэг — ручной выбор типа таблицы, не трогаем).
        var newPages = target.Pages
            .Select(p => new GostGroupingPage(p.PageIndex, GostUnifiedGroupingBuilder.StripPerPage(freshRows.GetValueOrDefault(p.PageIndex) ?? p.Fields)))
            .ToList();
        var freshName = newPages.Select(pg => pg.Fields.GetValueOrDefault("НаименованиеДокумента")).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var freshShifr = newPages.Select(pg => pg.Fields.GetValueOrDefault("Шифр")).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        // Сохраняем стабильный Id и тэги документа (issue #28) — только поля/шифр/имя/страницы свежие.
        groups[targetIdx] = target with
        {
            Code = string.IsNullOrWhiteSpace(freshShifr) ? target.Code : freshShifr,
            Name = string.IsNullOrWhiteSpace(freshName) ? target.Name : freshName,
            Pages = newPages,
        };
        var unified = new GostGroupingData(groups, grouping.ManuallyEdited);

        await MaterializeFileGroupingAsync(file, unified, bytes, ct);

        var pageCount = GetPdfPageCount(bytes);
        await notifications.PublishAsync(NotificationSeverity.Info, "Документ перераспознан",
            $"«{(string.IsNullOrWhiteSpace(freshName) ? freshShifr : freshName)}» — обновлены поля {target.Pages.Count} листов.", "Распознавание PDF", ct: ct);
        return new GostGroupingDto(
            unified.Groups.Select(g => new GostGroupingGroupDto(g.Kind, g.Code, g.Name, g.Pages.Select(p => p.PageIndex).ToList(), g.Tags)).ToList(),
            unified.ManuallyEdited, pageCount);
    }

    public async Task<GostGroupingDto?> ApplyGroupingAsync(Guid fileId, ApplyGroupingInput input, CancellationToken ct)
    {
        var file = await db.DataSetFiles.FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file == null) return null;
        if (file.Format != DataSetFormat.Pdf)
            throw new ArgumentException("Ручная корректировка разбиения доступна только для PDF-набора.");

        // Страница может не входить ни в одну группу (выпадает из реестров — допустимо), но
        // не может входить сразу в НЕСКОЛЬКО — иначе непонятно, какой группе она принадлежит.
        var seenPages = new HashSet<int>();
        foreach (var g in input.Groups)
            foreach (var p in g.PageIndices)
                if (!seenPages.Add(p))
                    throw new ArgumentException($"Страница {p + 1} назначена сразу нескольким группам.");

        await using var stream = await blob.DownloadAsync(file.BlobPath, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        // Существующая единая группировка: поля страниц (для проекции без потерь при переносе
        // страницы в другую группу — сохраняет её реальные распознанные поля).
        var existing = ParseGrouping(file.Grouping);
        var pageFields = new Dictionary<int, IReadOnlyDictionary<string, string?>>();
        if (existing is not null)
            foreach (var g in existing.Groups)
                foreach (var p in g.Pages)
                    pageFields[p.PageIndex] = p.Fields;

        // Новая единая группировка целиком из ввода (все виды: обложка/титул/документы).
        var unified = new GostGroupingData(
            input.Groups
                .Where(g => g.PageIndices.Count > 0)
                .Select(g => new GostGroupingGroup(g.Kind, g.Code, g.Name,
                    g.PageIndices.OrderBy(i => i)
                        .Select(i => new GostGroupingPage(i, pageFields.GetValueOrDefault(i) ?? new Dictionary<string, string?>()))
                        .ToList(),
                    g.Tags))
                .ToList(),
            ManuallyEdited: true);
        // Стабильные id (issue #28): переносим из существующей группировки по пересечению страниц.
        unified = GostStableIds.Assign(unified, existing);

        await MaterializeFileGroupingAsync(file, unified, bytes, ct);

        var pageCount = GetPdfPageCount(bytes);
        return new GostGroupingDto(
            unified.Groups.Select(g => new GostGroupingGroupDto(g.Kind, g.Code, g.Name, g.Pages.Select(p => p.PageIndex).ToList(), g.Tags)).ToList(),
            true, pageCount);
    }

    private async Task<int> GetPdfPageCountAsync(string blobPath, CancellationToken ct)
    {
        await using var stream = await blob.DownloadAsync(blobPath, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return GetPdfPageCount(ms.ToArray());
    }

    // Счётчик страниц из уже загруженных байтов — без повторного download+open, где PDF уже в памяти (P7).
    private static int GetPdfPageCount(byte[] bytes)
    {
        using var doc = PdfDocument.Open(bytes);
        return doc.NumberOfPages;
    }
}
