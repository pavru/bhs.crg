using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Recognition;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Recognition;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Создание и распознавание PDF-источников (профили "Счёт на оплату" и ГОСТ Р 21.101-2020
/// "Основная надпись") — растеризация/vision-LLM/группировка/физическое разрезание. Вынесено из
/// <see cref="DataSetService"/> (четвёртый шаг декомпозиции God Object'а, сделан внеочерёдно —
/// раньше запланированного места в очереди, т.к. фича ручной корректировки разбиения PDF целиком
/// принадлежит этому сервису по зависимостям; см. архитектурный отчёт, «Ручная корректировка
/// разбиения PDF»).
/// </summary>
public partial class DataSetPdfRecognitionService(
    AppDbContext db,
    IBlobStorage blob,
    IDocumentRecognizer recognizer,
    INotificationService notifications,
    IRecognitionProfileProvider profiles,
    ILogger<DataSetPdfRecognitionService> logger
)
{
    // Комплект чертежей может быть большим (десятки листов). Файл длиннее предела — отказ, а не
    // первые сто листов (issue #1271).
    private const int PdfRecognizeMaxPages = 100;

    /// <summary>
    /// Страницы файла картинками — ВСЕ либо отказ. Одно место на оба пути (нынешний и прежний
    /// постраничный реестр): два текста отказа об одном и том же разошлись бы первой же правкой.
    /// </summary>
    private static async Task<IReadOnlyList<byte[]>> RasterizeForRecognitionAsync(byte[] bytes, CancellationToken ct)
    {
        try
        {
            return await Task.Run(
                () => PdfRasterizer.ToPngPages(bytes, PdfRasterizer.DefaultDpi, PdfRecognizeMaxPages), ct);
        }
        catch (PdfPageLimitException ex)
        {
            throw TooLong(ex.Pages);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Сообщение растеризатора — в inner: оно чужое, а тип отказа наш (issue #1050).
            throw new InvalidRequestException(
                "Не удалось подготовить страницы PDF — файл повреждён или защищён.", ex);
        }
    }

    /// <summary>Не первые сто листов молча (issue #1271): недочитанный альбом неотличим от полного.</summary>
    private static InvalidRequestException TooLong(int pages) => new(
        $"В файле {RecognitionShared.Sheets(pages)}, а за один прогон распознаётся не больше " +
        $"{PdfRecognizeMaxPages} — разделите файл на части.");

    /// <summary>
    /// Тот же отказ — ДО постановки фоновой задачи: иначе нажатие получало 202, а отказ приходил
    /// строкой в журнале задач (ревью PR #1274). Окончательное слово всё равно за подготовкой страниц:
    /// здесь листы считает другая библиотека, и на файле, который она не прочла, молчим — причину
    /// назовёт сама работа.
    /// </summary>
    private async Task RefuseTooLongAsync(string blobPath, CancellationToken ct)
    {
        int pages;
        try
        {
            pages = await GetPdfPageCountAsync(blobPath, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return;
        }
        if (pages > PdfRecognizeMaxPages) throw TooLong(pages);
    }

    /// <summary>Выбор профиля препроцессинга PDF-набора (issue #38/#44). Оба профиля — набор-centric:
    /// ставим PreprocessingProfile на НАБОР, источников НЕ создаём. Распознавание пишет сырьё (Grouping
    /// для ГОСТ, InvoiceRawData для Счёта); кандидаты (обложка/титул/документы/таблицы либо шапка/
    /// товары) создаёт пользователь. Всегда возвращает null (источника нет).</summary>
    public async Task<DataSetSourceDto?> CreatePdfSourceAsync(Guid fileId, CreatePdfSourceInput input, CancellationToken ct)
    {
        var file = await db.DataSetFiles.FirstOrDefaultAsync(f => f.Id == fileId, ct)
            ?? throw new NotFoundException($"DataSetFile {fileId} not found");
        if (file.Format != DataSetFormat.Pdf)
            throw new InvalidRequestException("Файл не в формате PDF.");

        // Профиль называют явно (issue #1075). Прежде всё, что не «счёт», молча становилось ГОСТом —
        // и опечатка в названии, и профиль выключенного модуля давали набор, который потом нечем
        // прочитать.
        // Перечень — только доступное на этом экземпляре: назвать профиль выключенного модуля
        // значило бы посоветовать то, что следующим же запросом ответит отказом (ревью PR #1254).
        var offeredKinds = profiles.ListKinds().Select(k => k.Kind).ToHashSet(StringComparer.Ordinal);
        var known = string.Join(", ", PdfProfileRegistry.All
            .Where(p => offeredKinds.Contains(p.RequiredKind.ToString())).Select(p => p.ProfileMarker));
        if (string.IsNullOrWhiteSpace(input.Profile))
            throw new InvalidRequestException($"Не указан профиль распознавания PDF (поле profile). Известные: {known}.");
        var descriptor = PdfProfileRegistry.ByProfileMarker(input.Profile)
            ?? throw new InvalidRequestException(
                $"Неизвестный профиль распознавания PDF «{input.Profile}». Известные: {known}.");
        profiles.RequireKind(descriptor.RequiredKind);

        file.SetPreprocessingProfile(descriptor.ProfileMarker);
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>
    /// Ворота операций, которые есть только у альбома по ГОСТ: правка разбиения, таблица документа,
    /// старый постраничный реестр (issue #1075). Ядро модуль не называет — оно требует вида, без
    /// которого профиль PDF «ГОСТ» не читает ничего.
    /// </summary>
    private void RequireGost() => profiles.RequireKind(
        PdfProfileRegistry.ByProfileMarker(PdfProfiles.GostTitleBlock)!.RequiredKind);

    /// <summary>
    /// ВСЕ источники-проекции с этим маркером — их бывает больше одного (issue #1149).
    ///
    /// «Создать копию» (issue #717) заводит второй источник на том же маркере: те же строки, другой
    /// отбор. Пока распознавание брало «первый с маркером», свежие строки получала одна копия, а
    /// вторая оставалась со старыми и без пометки об устаревании — причём какая из двух, решал
    /// порядок строк в куче, и после правки любой из них это была уже другая. Сырьё у копий общее,
    /// поэтому и обновляются они вместе.
    /// </summary>
    private static IEnumerable<DataSetSource> ProjectionsOf(IEnumerable<DataSetSource> sources, string marker) =>
        sources.Where(s => s.SheetOrPath == marker);

    // ── Набор-centric распознавание ГОСТ (issue #38): всё по fileId, источников не создаёт ──

    /// <summary>Определяет профиль набора (issue #44) — по PreprocessingProfile, либо (обратная
    /// совместимость) по маркерам уже существующих источников для наборов, созданных до появления
    /// этого поля. Требует file.Sources загруженным.</summary>
    private static PdfProfileDescriptor ResolveDescriptor(Domain.DataSets.DataSetFile file) =>
        PdfProfileRegistry.ByProfileMarker(file.PreprocessingProfile)
        ?? file.Sources.Select(s => PdfProfileRegistry.BySourceMarker(s.SheetOrPath)).FirstOrDefault(d => d is not null)
        ?? throw new InvalidRequestException("У набора не выбран профиль распознавания PDF.");

    /// <summary>Планирование распознавания PDF-набора по fileId (issue #44: дискриминатор — профиль
    /// набора, не формат-специфичный код). ГОСТ — 409-проверка ручной правки разбиения + фон; Счёт —
    /// синхронно.</summary>
    public async Task<RecognizePlan?> PlanFileRecognitionAsync(Guid fileId, bool confirm, CancellationToken ct)
    {
        var file = await db.DataSetFiles.Include(f => f.Sources).AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null) return null;
        if (file.Format != DataSetFormat.Pdf)
            throw new InvalidRequestException("Набор не в формате PDF.");
        var descriptor = ResolveDescriptor(file);
        // Ворота — до постановки задачи: отказ «модуль выключен» из середины фоновой работы пришёл бы
        // строкой в журнале задач, а не ответом на нажатие.
        profiles.RequireKind(descriptor.RequiredKind);
        if (descriptor.Kind == PdfProfileKind.Gost)
        {
            var existingGrouping = ParseGrouping(file.Grouping);
            if (existingGrouping is { ManuallyEdited: true } && !confirm)
                throw new ConflictException(
                    "Разбиение набора было скорректировано вручную — повторное распознавание сотрёт ручные правки. Подтвердите, чтобы продолжить.");
            await RefuseTooLongAsync(file.BlobPath, ct);
        }
        return new RecognizePlan(descriptor.Background,
            descriptor.Kind == PdfProfileKind.Gost ? "Распознавание листов PDF" : "Распознавание PDF",
            file.Id);
    }

    /// <summary>Распознавание PDF-набора по НАБОРУ (issue #38/#44) — единая точка входа для всех профилей
    /// (unifies VERB вызова: раньше «Счёт» шёл через RecognizePdfSourceAsync(sourceId), теперь оба
    /// профиля — через fileId). ГОСТ пишет Grouping, источников не создаёт; Счёт — распознаёт и сразу
    /// обновляет уже созданную пару источников (шапка/товары — законно другая, не кандидатная модель).
    /// Кидает 409 при неподтверждённой ручной правке (только ГОСТ).</summary>
    public async Task RecognizeFileAsync(Guid fileId, bool confirm, CancellationToken ct, Func<int, int, Task>? onProgress = null)
    {
        var file = await db.DataSetFiles.Include(f => f.Sources).FirstOrDefaultAsync(f => f.Id == fileId, ct)
            ?? throw new NotFoundException($"DataSetFile {fileId} not found");
        if (file.Format != DataSetFormat.Pdf)
            throw new InvalidRequestException("Набор не в формате PDF.");
        var descriptor = ResolveDescriptor(file);
        profiles.RequireKind(descriptor.RequiredKind);

        if (descriptor.Kind == PdfProfileKind.Gost)
        {
            var existingGrouping = ParseGrouping(file.Grouping);
            if (existingGrouping is { ManuallyEdited: true } && !confirm)
                throw new ConflictException(
                    "Разбиение набора было скорректировано вручную — повторное распознавание сотрёт ручные правки. Подтвердите, чтобы продолжить.");
            await RecognizeGostFileAsync(file, ct, onProgress);
            return;
        }
        await RecognizeInvoiceFileAsync(file, ct);
    }
}
