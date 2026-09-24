using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Recognition;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.Persistence;
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
    // Комплект чертежей может быть большим (десятки листов) — выше, чем MaxPages=10 у
    // PdfRasterizer (тот подобран под сертификаты/декларации, не трогаем).
    private const int PdfRecognizeMaxPages = 100;

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

        file.SetPreprocessingProfile(input.Profile == PdfProfiles.Invoice ? PdfProfiles.Invoice : PdfProfiles.GostTitleBlock);
        await db.SaveChangesAsync(ct);
        return null;
    }

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
        if (descriptor.Kind == PdfProfileKind.Gost)
        {
            var existingGrouping = ParseGrouping(file.Grouping);
            if (existingGrouping is { ManuallyEdited: true } && !confirm)
                throw new ConflictException(
                    "Разбиение набора было скорректировано вручную — повторное распознавание сотрёт ручные правки. Подтвердите, чтобы продолжить.");
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
