using System.Text.Json;
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
/// Точечная работа по одному документу разбиения — часть <see cref="DataSetPdfRecognitionService" />.
///
/// <para>Перераспознавание таблицы документа, перераспознавание самого документа (только его
/// страницы, не весь альбом — vision дорог) и применение пользовательской правки границ.</para>
///
/// <para>Роднит их то, что все трое ПЕРЕПИСЫВАЮТ уже существующее разбиение, а не создают его:
/// правка здесь затрагивает данные, за которыми стоит дорогой прогон и ручной труд пользователя,
/// поэтому каждая из операций бережёт то, чего не касается (тэги, границы, распознанное сырьё
/// соседних групп).</para>
/// </summary>
public partial class DataSetPdfRecognitionService
{
    /// <summary>
    /// Распознаёт таблицу помеченного документа (тэг спецификации/кабельного журнала).
    ///
    /// <para>Результат — <b>сырьё набора</b>, а не источник: строки и колонки ложатся в
    /// <c>TableData</c>/<c>TableColumns</c> группы внутри <c>Grouping</c> (issue #42). Источник
    /// таблицы заводит ПОЛЬЗОВАТЕЛЬ, принимая кандидата «Таблица …»; здесь же, если он это уже
    /// сделал, обновляется кэш готовой проекции — это и есть путь повторного распознавания.</para>
    ///
    /// <para>⚠️ Ключ проекции — <c>gost-table:{id группы}</c>, а НЕ номер страницы. Параметром
    /// <paramref name="firstPageIndex" /> вызывающий лишь УКАЗЫВАЕТ, о каком документе речь;
    /// страницы документа меняются при правке разбиения, а id группы — нет. Прежняя редакция этого
    /// доккомментария обещала и создание источника, и маркер по первой странице: она описывала
    /// поведение до #42 и противоречила инлайн-комментарию двумя десятками строк ниже
    /// (ревью PR #1027).</para>
    ///
    /// <para>Распознавание — по образцу таблицы товаров счёта: под-PDF документа одним
    /// vision-вызовом, фиксированные колонки под тип (<c>GostTableFields</c>).</para>
    /// </summary>
    public async Task<GostGroupingDto?> RecognizeDocumentTableAsync(Guid fileId, int firstPageIndex, CancellationToken ct)
    {
        var file = await db.DataSetFiles.Include(f => f.Sources).FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file == null) return null;
        if (file.Format != DataSetFormat.Pdf)
            throw new InvalidRequestException("Распознавание таблицы доступно только для PDF-набора.");

        var grouping = ParseGrouping(file.Grouping);
        var group = grouping?.Groups.FirstOrDefault(
            g => g.Kind == GostGroupKind.Document && g.Pages.Any(p => p.PageIndex == firstPageIndex));
        if (group is null)
            throw new InvalidRequestException("Документ с указанной страницей не найден в группировке.");

        // ЕДИНАЯ цепочка приоритета (issue #410), а не параллельные механизмы:
        //   профиль, привязанный к группе  →  тип, объявивший тэг (#29)  →  встроенный профиль по тэгу.
        // Привязанный профиль СНИМАЕТ требование тэга — именно этим разблокируются произвольные
        // таблицы, для которых функционального тэга не существует и не может существовать.
        var spec = await ResolveTableSpecAsync(group, ct)
            ?? throw new InvalidRequestException(
                "У документа не задан ни профиль распознавания, ни тип таблицы — " +
                "привяжите профиль в свойствах группы либо укажите тип таблицы.");
        var (columns, kind, shape) = spec;

        // Под-PDF документа для vision: переиспользуем уже вырезанный при распознавании блок (group.BlobPath,
        // issue #38) — не режем заново. Fallback — вырезать из полного PDF (старые наборы без BlobPath).
        byte[] subPdf;
        if (!string.IsNullOrEmpty(group.BlobPath))
        {
            await using var s = await blob.DownloadAsync(group.BlobPath, ct);
            using var m = new MemoryStream();
            await s.CopyToAsync(m, ct);
            subPdf = m.ToArray();
        }
        else
        {
            await using var s = await blob.DownloadAsync(file.BlobPath, ct);
            using var m = new MemoryStream();
            await s.CopyToAsync(m, ct);
            var pageIndices = group.Pages.Select(p => p.PageIndex).OrderBy(i => i).ToList();
            try { subPdf = PdfPageSplitter.ExtractPages(m.ToArray(), pageIndices); }
            // Сообщение разборщика PDF — в inner, а не в текст: тип отказа наш, значит текст уходит
            // человеку дословно, и чужая половина уехала бы вместе с ним (issue #1050). Исключение
            // целиком запишет конвейер.
            catch (Exception ex)
            {
                throw new InvalidRequestException(
                    "Не удалось выделить страницы документа — файл PDF повреждён или защищён.", ex);
            }
        }

        // Промпт выбирает ВИД профиля (issue #406; прежде — прямое сравнение с тэгом, issue #389):
        // кабельный журнал имеет свой промпт (двойная форма По проекту/Проложен), иначе общий
        // табличный. Флаги формы — параметр профиля; у встроенных они дефолтные, т.е. промпт прежний.
        Func<IReadOnlyList<RecognitionField>, string> tablePrompt =
            kind == RecognitionProfileKind.CableJournal
                ? f => RecognitionShared.BuildCableJournalPrompt(f, shape)
                : f => RecognitionShared.BuildTablePrompt(f, shape);

        RecognitionResult result;
        try
        {
            result = await recognizer.RecognizeAsync(subPdf, "application/pdf",
                RecognitionKinds.ComposeCallFields(kind, [], columns), tablePrompt, ct: ct);
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

        var rows = GostTableFields.SplitRows(result.Values, columns);
        var schema = columns
            .Select(c => new DataSetColumnInfo(c.Path, rows.Take(3).Select(r => r.GetValueOrDefault(c.Path) ?? "").ToArray()))
            .ToArray();
        var schemaJson = DataSetDtoMapper.SerializeSchema(schema);
        var dataJson = JsonSerializer.Serialize(rows);
        var tableName = string.IsNullOrWhiteSpace(group.Name) ? "Таблица" : $"Таблица — {group.Name}";

        // issue #42: распознанная таблица — СЫРЬЁ набора, живёт на группе (в Grouping), а НЕ авто-источник.
        // Кандидат «Таблица …» проецирует её в источник по запросу пользователя (CreatePdfProjectionSource).
        var updated = grouping with
        {
            Groups = grouping.Groups
                .Select(gg => gg.Id == group.Id
                    ? gg with { TableData = dataJson, TableColumns = schemaJson, TableStale = false }
                    : gg)
                .ToList(),
        };
        file.SetGrouping(JsonSerializer.Serialize(updated));
        // Если пользователь УЖЕ создал источник-проекцию этой таблицы — обновляем его кэш (ре-распознавание).
        file.Sources.FirstOrDefault(s => s.SheetOrPath == $"{PdfProfiles.GostTableMarkerPrefix}{group.Id}")
            ?.UpdateCache(schemaJson, rows.Count, dataJson);
        await db.SaveChangesAsync(ct);

        // Ноль строк на ЯВНОМ вызове — предупреждение, а не отчёт об успехе (issue #803). Постранично
        // по альбому пустой результат законен: лист без штампа бывает. Здесь человек сам указал, что
        // на этих страницах таблица, — и «распознана, строк: 0» отвечает ему, что всё в порядке,
        // ровно тогда, когда не в порядке ничего.
        await notifications.PublishAsync(
            rows.Count == 0 ? NotificationSeverity.Warning : NotificationSeverity.Info,
            rows.Count == 0 ? "Таблица не распознана" : "Таблица распознана",
            rows.Count == 0
                ? $"«{tableName}» — модель не нашла в этих страницах ни одной строки таблицы. Проверьте границы документа и профиль распознавания."
                : $"«{tableName}» — строк: {rows.Count}. Доступна как кандидат — создайте из него источник в наборе.",
            "Распознавание PDF", audience: NotificationAudiences.DataSetsEdit, ct: ct);

        var pageCount = await GetPdfPageCountAsync(file.BlobPath, ct);
        return new GostGroupingDto(
            updated.Groups.Select(g => new GostGroupingGroupDto(g.Kind, g.Code, g.Name, g.Pages.Select(p => p.PageIndex).ToList(), g.Tags, g.ProfileId,
                g.Pages.Where(p => p.NoAnswer).Select(p => p.PageIndex).ToList())).ToList(),
            updated.ManuallyEdited, pageCount);
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
            throw new InvalidRequestException("Перераспознавание документа доступно только для PDF-набора.");

        var grouping = ParseGrouping(file.Grouping);
        var groups = grouping?.Groups.ToList();
        var targetIdx = groups?.FindIndex(g => g.Kind == GostGroupKind.Document && g.Pages.Any(p => p.PageIndex == firstPageIndex)) ?? -1;
        if (grouping is null || groups is null || targetIdx < 0)
            throw new InvalidRequestException("Документ с указанной страницей не найден в группировке.");
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
        var stampFields = (await ProfileForFileAsync(file, RecognitionProfileKind.TitleBlock, ct)).ToRecognitionFields();
        var fields = GostTitleBlockFields.WithClassifiers(stampFields);
        var freshRows = new Dictionary<int, IReadOnlyDictionary<string, string?>>();
        // Тот же накопитель, что и у полного прогона: отметки «ответа не было» нужны и здесь, иначе
        // перераспознавание документа снимало бы их со всех листов независимо от исхода.
        var docFailures = new PageFailureTracker();
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
            catch (Exception ex) when (ex is RecognitionTimeoutException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                // Таймаут ОДНОЙ страницы — её поля пусты, документ перераспознаётся дальше. Ловим
                // ПЕРЕД общей недоступностью, иначе таймаут (её наследник) уносил бы весь прогон,
                // выбрасывая уже обработанные страницы: до классификации таймаута он приходил сырым
                // OperationCanceledException и в эту ветку попадал сам собой.
                logger.LogWarning("Таймаут распознавания стр. {Page} при перераспознавании документа {FileId}", idx + 1, file.Id);
                values = fields.ToDictionary(f => f.Path, string? (f) => null);
                docFailures.PageFailed(new RecognitionTimeoutException("движок не ответил за отведённый срок."),
                    silent: false, pageIndex: idx);
            }
            catch (RecognitionSilentException ex)
            {
                // Молчание — свойство страницы, а не движка (issue #802): бросив здесь, мы выкинули
                // бы уже перераспознанные страницы документа ради одного листа, на котором модель
                // не ответила. Ловим ПЕРЕД общей недоступностью — Silent её наследник.
                logger.LogWarning("Стр. {Page} при перераспознавании документа {FileId}: ответа не было — {Msg}",
                    idx + 1, file.Id, ex.Message);
                values = fields.ToDictionary(f => f.Path, string? (f) => null);
                docFailures.PageFailed(ex, silent: true, pageIndex: idx);
            }
            catch (Exception ex) when (ex is RecognitionUnavailableException or RecognitionLimitException)
            {
                // Движок не работает вовсе (нет ключа, лимит, отказ) — перебирать оставшиеся страницы
                // незачем, каждая упрётся в то же самое.
                throw new InvalidRequestException($"Распознавание недоступно: {EngineRefusal.TextOf(ex)}", ex);
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
                    var cropResult = await recognizer.RecognizeAsync(cropPng, "image/png", stampFields, RecognitionShared.BuildTitleBlockPrompt, ct: ct);
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
        // Отметка «ответа не было» пересчитывается: перераспознавание для того и запускают, чтобы её
        // снять. Лист, на который свежий ответ пришёл, её теряет; лист, промолчавший снова, —
        // сохраняет (в freshRows он лежит с пустыми полями, см. catch выше).
        var newPages = target.Pages
            .Select(p => new GostGroupingPage(
                p.PageIndex,
                GostUnifiedGroupingBuilder.StripPerPage(freshRows.GetValueOrDefault(p.PageIndex) ?? p.Fields),
                docFailures.PagesWithoutAnswer.Contains(p.PageIndex)))
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
            $"«{(string.IsNullOrWhiteSpace(freshName) ? freshShifr : freshName)}» — обновлены поля {target.Pages.Count} листов.", "Распознавание PDF",
            audience: NotificationAudiences.DataSetsEdit, ct: ct);
        return new GostGroupingDto(
            unified.Groups.Select(g => new GostGroupingGroupDto(g.Kind, g.Code, g.Name, g.Pages.Select(p => p.PageIndex).ToList(), g.Tags, g.ProfileId,
                g.Pages.Where(p => p.NoAnswer).Select(p => p.PageIndex).ToList())).ToList(),
            unified.ManuallyEdited, pageCount);
    }

    public async Task<GostGroupingDto?> ApplyGroupingAsync(Guid fileId, ApplyGroupingInput input, CancellationToken ct)
    {
        var file = await db.DataSetFiles.FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file == null) return null;
        if (file.Format != DataSetFormat.Pdf)
            throw new InvalidRequestException("Ручная корректировка разбиения доступна только для PDF-набора.");

        // Страница может не входить ни в одну группу (выпадает из реестров — допустимо), но
        // не может входить сразу в НЕСКОЛЬКО — иначе непонятно, какой группе она принадлежит.
        var seenPages = new HashSet<int>();
        foreach (var g in input.Groups)
            foreach (var p in g.PageIndices)
                if (!seenPages.Add(p))
                    throw new InvalidRequestException($"Страница {p + 1} назначена сразу нескольким группам.");

        await using var stream = await blob.DownloadAsync(file.BlobPath, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        // Существующая единая группировка: поля страниц (для проекции без потерь при переносе
        // страницы в другую группу — сохраняет её реальные распознанные поля).
        var existing = ParseGrouping(file.Grouping);
        var pageFields = new Dictionary<int, IReadOnlyDictionary<string, string?>>();
        // «Ответа по листу не было» — свойство ЛИСТА, а не группы (issue #803): перенос страницы в
        // другую группу его не отменяет, как не отменяет и распознанных полей. Без этого первое же
        // «Сохранить» стирало бы отметки, и признак жил бы ровно до первой ручной правки.
        var pageNoAnswer = new HashSet<int>();
        if (existing is not null)
            foreach (var g in existing.Groups)
                foreach (var p in g.Pages)
                {
                    pageFields[p.PageIndex] = p.Fields;
                    if (p.NoAnswer) pageNoAnswer.Add(p.PageIndex);
                }

        // Новая единая группировка целиком из ввода (все виды: обложка/титул/документы).
        var unified = new GostGroupingData(
            input.Groups
                .Where(g => g.PageIndices.Count > 0)
                .Select(g => new GostGroupingGroup(g.Kind, g.Code, g.Name,
                    g.PageIndices.OrderBy(i => i)
                        .Select(i => new GostGroupingPage(i, pageFields.GetValueOrDefault(i) ?? new Dictionary<string, string?>(),
                            pageNoAnswer.Contains(i)))
                        .ToList(),
                    g.Tags))
                .ToList(),
            ManuallyEdited: true);
        // Стабильные id (issue #28): переносим из существующей группировки по пересечению страниц.
        unified = GostStableIds.Assign(unified, existing);

        await MaterializeFileGroupingAsync(file, unified, bytes, ct);

        var pageCount = GetPdfPageCount(bytes);
        return new GostGroupingDto(
            unified.Groups.Select(g => new GostGroupingGroupDto(g.Kind, g.Code, g.Name, g.Pages.Select(p => p.PageIndex).ToList(), g.Tags, g.ProfileId,
                g.Pages.Where(p => p.NoAnswer).Select(p => p.PageIndex).ToList())).ToList(),
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
