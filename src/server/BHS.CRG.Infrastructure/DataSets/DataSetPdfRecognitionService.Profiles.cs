using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Notifications;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Infrastructure.Recognition;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Как именно распознаётся файл каждого профиля — часть <see cref="DataSetPdfRecognitionService" />.
///
/// <para>«Счёт на оплату» — один вызов на весь многостраничный PDF. ГОСТ — постраничный цикл с
/// классификаторами вида страницы и последующей группировкой, а затем материализация сырья:
/// разрезание под-PDF каждой группы, переспроецирование УЖЕ СОЗДАННЫХ источников-проекций, чистка
/// осиротевших блобов и итоговое уведомление о прогоне.</para>
///
/// <para>Оба профиля вместе, потому что это одно занятие — реализация за единой точкой входа.
/// Роднит их и главное правило: ни один источников НЕ создаёт (issue #38/#44), сырьё ложится на
/// набор, а источники заводит пользователь, принимая кандидатов.</para>
/// </summary>
public partial class DataSetPdfRecognitionService
{
    /// <summary>
    /// Профиль "Счёт на оплату" (issue #44: набор-centric — сырьё на набор, БЕЗ авто-создания источников,
    /// как у ГОСТ) — один вызов распознавания на весь многостраничный PDF (Gemini/Anthropic принимают
    /// application/pdf целиком без растеризации; Ollama растеризует сама внутри движка) вместо цикла по
    /// страницам. Результат пишется как СЫРЬЁ в DataSetFile.InvoiceRawData — кандидаты «Шапка»/«Товары»
    /// проецируются пользователем (см. DataSetSourceService.InvoiceCandidatesAsync/
    /// CreateInvoiceProjectionSourceAsync). Если пользователь УЖЕ создал источник-проекцию — обновляем
    /// его кэш (ре-распознавание), тем же паттерном, что RefreshProjectionSourcesAsync у ГОСТ.
    /// Требует file.Sources загруженным.
    /// </summary>
    private async Task RecognizeInvoiceFileAsync(Domain.DataSets.DataSetFile file, CancellationToken ct)
    {
        await using var stream = await blob.DownloadAsync(file.BlobPath, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        // Счёт — ОДИН вызов: шапка (скаляры) и товары (колонки строк) лежат в одном профиле.
        var invoiceProfile = await ProfileForFileAsync(file, RecognitionProfileKind.Invoice, ct);
        var headerFields = invoiceProfile.ToRecognitionFields();
        var lineItemFields = invoiceProfile.ToRowColumns();

        RecognitionResult result;
        try
        {
            result = await recognizer.RecognizeAsync(bytes, "application/pdf",
                RecognitionKinds.ComposeCallFields(invoiceProfile), RecognitionShared.BuildInvoicePrompt, ct: ct);
        }
        catch (RecognitionSilentException ex)
        {
            // Одиночный вызов по прямой просьбе человека: он указал, ЧТО распознать, и «ответа не
            // было» тут не страничная случайность, а результат. Отдельно от «недоступно» ради
            // текста: движок работает, но ответа не отдал, и совет проверять настройки был бы ложью.
            throw new InvalidRequestException($"Модель не отдала ответ: {EngineRefusal.TextOf(ex)}", ex);
        }
        catch (Exception ex) when (ex is RecognitionUnavailableException or RecognitionLimitException)
        {
            throw new InvalidRequestException($"Распознавание недоступно: {EngineRefusal.TextOf(ex)}", ex);
        }

        var headerRow = InvoiceRecognitionSplitter.SplitHeader(result.Values, headerFields);
        // Сломанный/не-JSON ответ модели по товарам — InvoiceRecognitionSplitter молча вернёт []
        // (шапка уже распозналась независимо, та же философия, что и у постраничного профиля).
        var lineItemRows = InvoiceRecognitionSplitter.SplitLineItems(result.Values);

        file.SetInvoiceRawData(JsonSerializer.Serialize(new InvoiceRawData(headerRow, lineItemRows)));

        var header = file.Sources.FirstOrDefault(s => s.SheetOrPath == PdfProfiles.InvoiceHeaderMarker);
        if (header is not null)
        {
            var headerColumns = headerFields
                .Select(f => new DataSetColumnInfo(f.Path, [headerRow.GetValueOrDefault(f.Path) ?? ""]))
                .ToArray();
            header.UpdateCache(DataSetDtoMapper.SerializeSchema(headerColumns), 1, JsonSerializer.Serialize(new[] { headerRow }));
        }
        var lineItems = file.Sources.FirstOrDefault(s => s.SheetOrPath == PdfProfiles.InvoiceLineItemsMarker);
        if (lineItems is not null)
        {
            var lineItemColumns = lineItemFields
                .Select(f => new DataSetColumnInfo(f.Path,
                    lineItemRows.Take(3).Select(r => r.GetValueOrDefault(f.Path) ?? "").ToArray()))
                .ToArray();
            lineItems.UpdateCache(DataSetDtoMapper.SerializeSchema(lineItemColumns), lineItemRows.Count, JsonSerializer.Serialize(lineItemRows));
        }

        await db.SaveChangesAsync(ct);
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
            // Сообщение растеризатора — в inner: оно чужое, а тип отказа наш (issue #1050).
            throw new InvalidRequestException(
                "Не удалось подготовить страницы PDF — файл повреждён или защищён.", ex);
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

        var stampFields = (await ProfileForFileAsync(file, RecognitionProfileKind.TitleBlock, ct)).ToRecognitionFields();
        var fields = GostTitleBlockFields.WithClassifiers(stampFields);
        var coverFields = (await ProfileForFileAsync(file, RecognitionProfileKind.CoverTitle, ct)).ToRecognitionFields();
        var rows = new List<IReadOnlyDictionary<string, string?>>();
        // Счётчик листов без ответа, первая причина и отсечка «движок замолчал» — общим накопителем
        // на все постраничные прогоны (issue #801, #802).
        var failures = new PageFailureTracker();
        // Кем распозналось — в уведомление (issue #803): «почему у меня плохо распозналось» спрашивают
        // после прогона, и ответ начинается с имени движка и модели. ВСЕ участвовавшие, а не первый:
        // цепочка может переключиться на середине альбома (у облачного кончилась квота), и назвать
        // движок, который проблемных листов не касался, хуже, чем не называть никого.
        var enginesUsed = new List<string>();
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
                if (result.Engine is { } usedEngine && !enginesUsed.Contains(usedEngine)) enginesUsed.Add(usedEngine);
                failures.PageSucceeded();
            }
            catch (RecognitionSilentException ex)
            {
                // Молчание — НЕ повод бросить прогон на первой же странице, в отличие от таймаута:
                // лист мог оказаться нечитаемым для модели, а следующие пятнадцать — нормальными.
                // Отсечка тут другая — подряд идущие молчания, и она ПРЕКРАЩАЕТ прогон, а не
                // отменяет его: распознанное сохраняется.
                logger.LogWarning("Страница {Page} источника {SourceId}: ответа не было — {Msg}", i + 1, file.Id, ex.Message);
                rows.Add(fields.ToDictionary(f => f.Path, string? (f) => null));
                failures.PageFailed(ex, silent: true, pageIndex: i);
                if (failures.ShouldStop)
                {
                    logger.LogWarning("Прогон набора {SourceId} прекращён: {Reason}", file.Id, failures.StopReason);
                    // Остальные листы дописываем пустыми, чтобы строки соответствовали страницам, —
                    // и выходим к материализации: сорок девять распознанных листов из двухсот
                    // человеку нужнее, чем отменённая задача.
                    for (var rest = i + 1; rest < pngPages.Count; rest++)
                    {
                        rows.Add(fields.ToDictionary(f => f.Path, string? (f) => null));
                        // Эти листы движку даже не показывали — «ответа не было» про них тем более
                        // правда, и молча выдавать их за пустые нельзя.
                        failures.MarkNotAttempted(rest);
                    }
                    break;
                }
                continue;
            }
            catch (Exception ex) when (ex is RecognitionUnavailableException or RecognitionLimitException)
            {
                // Таймаут (RecognitionTimeoutException) — тоже сюда: на первой странице прогон
                // прекращается, не тратя часы на сотню страниц у движка, который не ответил ни
                // одной, а дальше первой строка остаётся пустой. Это и есть прежнее поведение,
                // только теперь по классифицированному типу, а не по сырой отмене.
                if (i == 0)
                    throw new InvalidRequestException($"Распознавание недоступно: {EngineRefusal.TextOf(ex)}", ex);
                logger.LogWarning(ex, "Распознавание страницы {Page} источника {SourceId} не удалось — строка останется пустой", i + 1, file.Id);
                rows.Add(fields.ToDictionary(f => f.Path, string? (f) => null));
                failures.PageFailed(ex, silent: false, pageIndex: i);
                continue;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Страховка: отмена при живом токене из мест, которые классификацию не проходят
                // (внутренние сроки, будущие движки).
                logger.LogWarning("Таймаут распознавания страницы {Page} источника {SourceId} — строка останется пустой", i + 1, file.Id);
                rows.Add(fields.ToDictionary(f => f.Path, string? (f) => null));
                failures.PageFailed(new RecognitionTimeoutException("движок не ответил за отведённый срок."), silent: false, pageIndex: i);
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
                        pngPages[i], "image/png", coverFields, RecognitionShared.BuildCoverTitlePrompt, ct: ct);
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
        // carryUserData: полное ре-распознавание переносит тэги/табличное сырьё с прежних групп по
        // стабильному id (свежие группы приходят без тэгов — иначе пользовательская разметка потерялась бы).
        var unified = GostStableIds.Assign(
            GostUnifiedGroupingBuilder.Build(routed, rows, manuallyEdited: false, failures.PagesWithoutAnswer),
            existingGrouping, carryUserData: true);

        // Материализация СЫРЬЯ на наборе: режем под-PDF в группы (BlobPath в Grouping), пишем Grouping,
        // переспроецируем существующие источники-проекции, чистим осиротевшие блобы. Источников НЕ создаём.
        var matResult = await MaterializeFileGroupingAsync(file, unified, bytes, ct);
        // «В наборе не появилось ничего» считаем ЗДЕСЬ, где строки под рукой: уведомление называет
        // это полным провалом, и признак должен совпадать с обещанием текста.
        var nothingRecognized = rows.All(r => r.Values.All(string.IsNullOrWhiteSpace));
        await PublishGostRecognitionResultAsync(matResult.DocumentCount, rows.Count, failures.FailedPages,
            failures.ShouldStop
                ? $"{failures.StopReason} Ещё {failures.NotAttemptedPages} листов не обработаны."
                : failures.FirstReason,
            nothingRecognized, matResult.FailedSplits, matResult.InvalidatedTables,
            enginesUsed.Count > 0 ? string.Join(", ", enginesUsed) : null, ct);
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
        await RefreshProjectionSourcesAsync(file, projected, ct);
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
    private async Task RefreshProjectionSourcesAsync(
        Domain.DataSets.DataSetFile file, ProjectedRows projected, CancellationToken ct)
    {
        var fileId = file.Id;
        // Колонки проекций берём из ТЕХ ЖЕ профилей, которыми распознавали, — иначе добавленное
        // пользователем поле не доехало бы до схемы источника.
        var coverColumnPaths = (await ProfileForFileAsync(file, RecognitionProfileKind.CoverTitle, ct))
            .Fields.Select(f => f.Name).ToArray();
        var documentsColumnPaths = (await ProfileForFileAsync(file, RecognitionProfileKind.TitleBlock, ct))
            .Fields.Select(f => f.Name)
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
        int documentCount, int pageCount, int failedPages, string? failureReason, bool nothingRecognized,
        int failedSplits, int invalidatedTables, string? engine, CancellationToken ct)
    {
        var (severity, title, msg) = DescribeGostResult(
            documentCount, pageCount, failedPages, failureReason, nothingRecognized, failedSplits, invalidatedTables, engine);
        await notifications.PublishAsync(severity, title, msg, "Распознавание PDF",
            audience: NotificationAudiences.DataSetsEdit, ct: ct);
    }

    /// <summary>
    /// Чем отчитаться о прогоне: серьёзность, заголовок, текст. Отдельной чистой функцией, потому что
    /// это единственное место, где решается, назвать прогон провалом или пропусками, — и решение
    /// такого рода обязано быть проверяемым. Прежний признак полного провала был недостижим и
    /// выглядел рабочим; нашли это чтением, потому что смотреть прогоном было не на что (issue #801).
    ///
    /// Открыт ради теста — как <c>ModelGone.AdviceFrom</c> и по той же причине.
    /// </summary>
    public static (NotificationSeverity Severity, string Title, string Message) DescribeGostResult(
        int documentCount, int pageCount, int failedPages, string? failureReason, bool nothingRecognized,
        int failedSplits, int invalidatedTables, string? engine = null)
    {
        // «Не ответила», а не «не распозналось»: пустой штамп — законный исход, и обвинять модель в
        // нём незачем. Речь именно о листах, ответа по которым не было вовсе.
        var msg = $"Распознано: {documentCount} документов, {pageCount} листов.";
        if (failedPages > 0) msg += $" Модель не ответила по листам: {failedPages}.";
        // Точку добавляем сами: тексты причин приходят из разных мест (сообщение исключения движка,
        // наша формулировка про таймаут), и половина из них её не несёт.
        if (!string.IsNullOrWhiteSpace(failureReason))
        {
            var reason = failureReason.TrimEnd();
            msg += $" Причина: {reason}" + (reason.EndsWith('.') ? "" : ".");
        }
        if (failedSplits > 0) msg += $" Документов без файла: {failedSplits}.";
        if (invalidatedTables > 0)
            msg += $" Табличные источники ({invalidatedTables}) инвалидированы — границы документов изменились," +
                   " проверьте/перераспознайте их вручную.";

        // Кем распознавали — в конце, отдельной фразой: при удачном прогоне это справка, при
        // неудачном — первое, что нужно знать, чтобы понимать, что менять.
        if (!string.IsNullOrWhiteSpace(engine)) msg += $" Распознавал: {engine}.";

        var hasIssues = failedPages > 0 || failedSplits > 0 || invalidatedTables > 0;
        // Не распозналось НИ ОДНОГО листа — это не «завершено с пропусками», а полный провал, и
        // называть его предупреждением значит приуменьшать: в наборе не появилось ничего.
        //
        // Условие именно «все строки пусты», а не «failedPages == pageCount»: второе недостижимо и
        // выглядело бы рабочим. Провал ПЕРВОГО листа по классифицированному пути прекращает прогон
        // ещё до уведомления (не тратим часы на движок, не ответивший ни разу), так что счётчик
        // никогда не сравняется с числом листов. Достижимый случай выглядит иначе: первый лист
        // ответил пустотой (законный исход — лист без штампа), остальные отвалились по отказу.
        //
        // Обратная сторона границы: если первый лист распознался, а следующие двести отвалились, это
        // всё-таки Warning — в наборе что-то появилось. Порога вроде «девяносто процентов отказов»
        // здесь намеренно нет: число пришлось бы выдумать, а причина отказа теперь названа в тексте
        // и без него.
        var total = failedPages > 0 && nothingRecognized;
        return (
            total ? NotificationSeverity.Error : hasIssues ? NotificationSeverity.Warning : NotificationSeverity.Info,
            total ? "Распознавание PDF не удалось" : "Распознавание групп листов PDF завершено",
            msg);
    }

    // Тип поля схемы → тип поля распознавания (консервативно: число/дата, остальное — строка).
    private static string MapRecognitionType(string schemaType) => schemaType switch
    {
        "number" => "number",
        "date" => "date",
        _ => "string",
    };

    /// <summary>
    /// Делегат к общему санитайзеру, а не своя копия. Копия здесь и была — дословная, с тем же
    /// платформенным изъяном: на Linux <c>GetInvalidFileNameChars()</c> это всего {'\0', '/'},
    /// и группа с именем «АОСР\1» уезжала обратным слэшем прямо в ключ объекта в хранилище
    /// (issue #854). Ключи блобов живут долго — расходиться реализациям тут нельзя.
    /// </summary>
    private static string SanitizeFileName(string name) => FileNames.Sanitize(name, "документ");

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

        // Стабильные id текущих документов, всё ещё ТАБЛИЧНЫХ (issue #28). Предикат — общий резолвер
        // (issue #410): «привязан профиль ИЛИ есть табличный тэг». Критично: не попавшая сюда группа
        // считается осиротевшей, и её источник удаляется ВМЕСТЕ С ПРИВЯЗКАМИ — оставь здесь проверку
        // только по тэгу, и источники произвольных таблиц сносились бы при каждой ре-группировке.
        var validGroups = new Dictionary<Guid, GostGroupingGroup>();
        foreach (var g in unified.Groups)
        {
            if (g.Kind != GostGroupKind.Document || g.Pages.Count == 0) continue;
            if (await IsTableGroupAsync(g, ct)) validGroups[g.Id] = g;
        }

        var removed = 0;
        foreach (var ts in tableSources)
        {
            var idStr = ts.SheetOrPath[PdfProfiles.GostTableMarkerPrefix.Length..];
            if (Guid.TryParse(idStr, out var gid) && validGroups.TryGetValue(gid, out var g))
            {
                // Документ сохранился (id совпал) — РЕ-ПРОЕЦИРУЕМ источник из нового табличного сырья группы
                // (issue #42). TableData перенесено GostStableIds.Assign; при смене состава страниц оно
                // помечено stale — пробрасываем на источник (пользователь перераспознает таблицу).
                if (!string.IsNullOrEmpty(g.TableData))
                {
                    var rowCount = JsonSerializer.Deserialize<List<Dictionary<string, string?>>>(g.TableData)?.Count ?? 0;
                    ts.UpdateCache(g.TableColumns ?? "[]", rowCount, g.TableData);
                    if (g.TableStale) ts.MarkRecognitionStale(DataSetStaleReason.TableBoundariesChanged);
                }
                continue;
            }

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
}
