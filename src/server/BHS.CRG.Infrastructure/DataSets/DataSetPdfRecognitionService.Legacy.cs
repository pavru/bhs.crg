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

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Source-centric путь распознавания — часть <see cref="DataSetPdfRecognitionService" />.
///
/// <para>Штатный вход давно по набору: <c>RecognizeFileAsync(fileId)</c> в основном файле. Здесь
/// лежит прежняя пара по <c>sourceId</c>, сохранённая для существующих call-sites (эндпоинт
/// <c>POST /sources/{id}/recognize</c>): дискриминатор профиля берётся из маркера ИСТОЧНИКА, а не
/// из поля набора (issue #44).</para>
///
/// <para>Отдельным файлом — чтобы два пути не читались как один: оба зовутся «распознать», но
/// ходят от разных сущностей, и смешение их в одном списке методов и было главной причиной
/// перепутать, какой из них штатный.</para>
/// </summary>
public partial class DataSetPdfRecognitionService
{
    /// <summary>Legacy source-centric планирование (issue #44: дискриминатор через дескриптор по
    /// маркеру источника). Новый штатный путь — PlanFileRecognitionAsync(fileId); сохранён для
    /// существующих call-sites (эндпоинт POST /sources/{id}/recognize).</summary>
    public async Task<RecognizePlan?> PlanRecognitionAsync(Guid sourceId, bool confirm, CancellationToken ct)
    {
        var source = await db.DataSetSources.Include(s => s.File).AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source is null) return null;
        if (source.File.Format != DataSetFormat.Pdf)
            throw new InvalidRequestException("Источник не относится к PDF-файлу.");

        var descriptor = PdfProfileRegistry.BySourceMarker(source.SheetOrPath);
        if (descriptor?.Kind == PdfProfileKind.Gost)
        {
            // 409-проверка ручной правки — ДО постановки в фон (чтобы диалог подтверждения был интерактивным).
            // Группировка живёт на НАБОРЕ (issue #28), не на источнике.
            var existingGrouping = ParseGrouping(source.File.Grouping);
            if (existingGrouping is { ManuallyEdited: true } && !confirm)
                throw new ConflictException(
                    "Разбиение этого источника было скорректировано вручную — повторное распознавание сотрёт ручные правки. Подтвердите, чтобы продолжить.");
            return new RecognizePlan(descriptor.Background, Title: "Распознавание листов PDF", source.File.Id);
        }
        // Таблица документа — один vision-вызов на под-PDF; счёт/legacy тоже короткие. Синхронно.
        if (source.SheetOrPath.StartsWith(PdfProfiles.GostTableMarkerPrefix, StringComparison.Ordinal))
            return new RecognizePlan(Background: false, Title: "Распознавание таблицы документа", source.File.Id);
        if (descriptor is null && source.SheetOrPath != PdfProfiles.LegacyTitleBlockRegistryMarker)
            throw new InvalidRequestException(
                $"Источник «{source.Name}» не распознаётся по отдельности — запустите распознавание набора.");
        return new RecognizePlan(Background: false, Title: "Распознавание PDF", source.File.Id);
    }

    /// <summary>Legacy source-centric распознавание (issue #44: дискриминатор через дескриптор). Новый
    /// штатный путь для обоих профилей — RecognizeFileAsync(fileId) (unifies VERB вызова); сохранён для
    /// существующих call-sites.</summary>
    public async Task<DataSetSourceDto?> RecognizePdfSourceAsync(Guid sourceId, bool confirm, CancellationToken ct,
        Func<int, int, Task>? onProgress = null)
    {
        var source = await db.DataSetSources.Include(s => s.File).ThenInclude(f => f.Sources)
            .FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;
        if (source.File.Format != DataSetFormat.Pdf)
            throw new InvalidRequestException("Источник не относится к PDF-файлу.");

        var descriptor = PdfProfileRegistry.BySourceMarker(source.SheetOrPath);

        if (descriptor?.Kind == PdfProfileKind.InvoiceFixedSlices)
        {
            await RecognizeInvoiceFileAsync(source.File, ct);
            return DataSetDtoMapper.MapSource(source);
        }

        if (descriptor?.Kind == PdfProfileKind.Gost)
        {
            // Ручная правка группировки — дороже автораспознавания LLM-вызовов (пользователь
            // руками разбирал документы) — не затираем без явного согласия.
            // Группировка живёт на НАБОРЕ (issue #28).
            var existingGrouping = ParseGrouping(source.File.Grouping);
            if (existingGrouping is { ManuallyEdited: true } && !confirm)
                throw new ConflictException(
                    "Разбиение этого источника было скорректировано вручную — повторное распознавание сотрёт ручные правки. Подтвердите, чтобы продолжить.");

            // Мост для legacy source-centric вызова: делегируем в набор-centric распознавание (issue #38).
            // Новый штатный путь — RecognizeFileAsync(fileId); этот сохранён для существующих call-sites.
            await RecognizeGostFileAsync(source.File, ct, onProgress);
            return DataSetDtoMapper.MapSource(source);
        }

        // Табличная проекция документа: перераспознаём ИМЕННО её таблицу, а не файл (issue #815).
        // Без этой ветки вызов проваливался в legacy-путь ниже и прогонял по всему альбому
        // распознавание ШТАМПОВ, записывая их строки в табличный источник, — то есть кнопка
        // «Перераспознать» у таблицы стирала бы таблицу. Дверь была открыта, но из UI в неё никто
        // не ходил: хука на этот эндпоинт не существовало.
        if (source.SheetOrPath.StartsWith(PdfProfiles.GostTableMarkerPrefix, StringComparison.Ordinal))
        {
            var idStr = source.SheetOrPath[PdfProfiles.GostTableMarkerPrefix.Length..];
            var grouping = ParseGrouping(source.File.Grouping);
            var group = Guid.TryParse(idStr, out var gid)
                ? grouping?.Groups.FirstOrDefault(g => g.Id == gid)
                : null;
            if (group is null || group.Pages.Count == 0)
                throw new InvalidRequestException(
                    "Документ этой таблицы больше не существует в разбиении — проверьте разбиение набора.");
            await RecognizeDocumentTableAsync(source.File.Id, group.Pages[0].PageIndex, ct);
            var refreshed = await db.DataSetSources.AsNoTracking().FirstAsync(x => x.Id == sourceId, ct);
            return DataSetDtoMapper.MapSource(refreshed);
        }

        // Дальше — legacy-путь для источников, созданных до тройки обложка/титул/документы
        // (маркер "titleblock-registry") — постраничный плоский реестр без группировки/
        // разрезания, поведение не меняем. Всё остальное сюда попадать не должно: постраничный
        // реестр штампов, записанный в чужой источник, — не распознавание, а порча данных.
        if (source.SheetOrPath != PdfProfiles.LegacyTitleBlockRegistryMarker)
            throw new InvalidRequestException(
                $"Источник «{source.Name}» не распознаётся по отдельности — запустите распознавание набора.");
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
            // Сообщение растеризатора — в inner: оно чужое, а тип отказа наш (issue #1050).
            throw new InvalidRequestException(
                "Не удалось подготовить страницы PDF — файл повреждён или защищён.", ex);
        }

        var fields = (await ProfileForFileAsync(source.File, RecognitionProfileKind.TitleBlock, ct)).ToRecognitionFields();
        var rows = new List<IReadOnlyDictionary<string, string?>>();
        // Тот же накопитель, что и у прогона набора: этот цикл до issue #802 не считал отказы вовсе —
        // страницы молча уходили пустыми, и узнать об этом было неоткуда.
        var failures = new PageFailureTracker();
        var sourceEngines = new List<string>();
        for (var i = 0; i < pages.Count; i++)
        {
            try
            {
                var result = await recognizer.RecognizeAsync(
                    pages[i], "image/png", fields, RecognitionShared.BuildTitleBlockPrompt, ct: ct);
                rows.Add(result.Values);
                if (result.Engine is { } used && !sourceEngines.Contains(used)) sourceEngines.Add(used);
                failures.PageSucceeded();
            }
            catch (RecognitionSilentException ex)
            {
                // Молчание страничное: прогон на первой странице не роняем, но подряд идущее
                // молчание прекращает работу — с сохранением уже распознанного.
                logger.LogWarning("Страница {Page} источника {SourceId}: ответа не было — {Msg}", i + 1, sourceId, ex.Message);
                rows.Add(fields.ToDictionary(f => f.Path, string? (f) => null));
                failures.PageFailed(ex, silent: true, pageIndex: i);
                if (failures.ShouldStop)
                {
                    logger.LogWarning("Прогон источника {SourceId} прекращён: {Reason}", sourceId, failures.StopReason);
                    for (var rest = i + 1; rest < pages.Count; rest++)
                    {
                        rows.Add(fields.ToDictionary(f => f.Path, string? (f) => null));
                        failures.MarkNotAttempted(rest);
                    }
                    break;
                }
            }
            catch (Exception ex) when (ex is RecognitionUnavailableException or RecognitionLimitException)
            {
                // Первая же страница — вероятно, движки не настроены вообще: нет смысла повторять
                // ту же ошибку ещё N-1 раз, сообщаем сразу. Дальше по комплекту — считаем
                // страницо-специфичной проблемой (не роняем весь реестр, см. фикс невычислимых
                // колонок XPath/JSONPath той же сессии), строка остаётся пустой.
                if (i == 0)
                    throw new InvalidRequestException($"Распознавание недоступно: {EngineRefusal.TextOf(ex)}", ex);
                logger.LogWarning(ex, "Распознавание страницы {Page} источника {SourceId} не удалось — строка останется пустой", i + 1, sourceId);
                rows.Add(fields.ToDictionary(f => f.Path, string? (f) => null));
                failures.PageFailed(ex, silent: false);
            }
        }
        if (failures.FailedPages > 0)
        {
            logger.LogWarning("Источник {SourceId}: листов без ответа {Failed} из {Total}. Причина: {Reason}",
                sourceId, failures.FailedPages, pages.Count, failures.FirstReason);
            // Уведомления у этого цикла не было ВОВСЕ: страницы уходили пустыми, и узнать об этом
            // человеку было неоткуда — прогон выглядел удавшимся (issue #803).
            var reason = failures.ShouldStop ? failures.StopReason : failures.FirstReason;
            await notifications.PublishAsync(NotificationSeverity.Warning,
                "Распознавание источника завершено с пропусками",
                // «Листов в источнике», а не «обработано»: при остановке по счётчику молчаний часть
                // листов движку не показывали вовсе, и назвать их обработанными было бы неправдой.
                $"Листов в источнике: {pages.Count}. Модель не ответила по листам: {failures.FailedPages}." +
                // Пропущенные — отдельным числом: назвав только неотвеченные, сообщение о прогоне,
                // прекращённом на третьем листе из двухсот, говорило бы про три, а пустых строк было
                // бы сто девяносто семь.
                (failures.NotAttemptedPages > 0 ? $" Прогон прекращён, ещё {failures.NotAttemptedPages} листов не обработаны." : "") +
                (string.IsNullOrWhiteSpace(reason) ? "" : $" Причина: {reason.TrimEnd('.')}.") +
                (sourceEngines.Count == 0 ? "" : $" Распознавал: {string.Join(", ", sourceEngines)}."),
                "Распознавание PDF", audience: NotificationAudiences.DataSetsEdit, ct: ct);
        }

        var columns = fields.Select(f => new DataSetColumnInfo(f.Path,
            rows.Take(3).Select(r => r.TryGetValue(f.Path, out var v) ? v ?? "" : "").ToArray()
        )).ToArray();

        source.UpdateCache(DataSetDtoMapper.SerializeSchema(columns), rows.Count, JsonSerializer.Serialize(rows));
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapSource(source);
    }
}
