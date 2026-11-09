using System.Globalization;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Endpoints;

/// <summary>
/// Ответы приложений к смете (change <c>plan-11-conjuncture</c>): одним местом на список, принятие и страницы.
/// Цены анализа — только с <c>plan.estimate.cost</c> (D4 зонтика): без права их полей в ответе нет вовсе, а корень
/// называет скрытое (<c>hidden</c>).
/// </summary>
internal static class ConjunctureViews
{
    /// <summary>Позиций и строк на странице — как у строк редакции.</summary>
    public const int DefaultLimit = EstimateRowsEndpoints.DefaultLimit;

    public const int MaxLimit = EstimateRowsEndpoints.MaxLimit;

    public static async Task<List<EstimateAttachmentDto>> ListAsync<T>(
        PlanDbContext db, IQueryable<T> attachments, CancellationToken ct) where T : EstimateAttachment
    {
        var rows = await attachments.AsNoTracking().OrderByDescending(a => a.ImportedAt).ThenBy(a => a.Id)
            .Select(a => new { Attachment = a, Version = EF.Property<uint>(a, PlanDbContext.RowVersion) })
            .ToListAsync(ct);
        var ids = rows.Select(r => r.Attachment.Id).ToList();
        var summary = typeof(ConjunctureAnalysis).IsAssignableFrom(typeof(T))
            ? await AnalysisSummaryAsync(db, ids, ct)
            : await StatementSummaryAsync(db, ids, ct);
        return [.. rows.Select(r => Dto(r.Attachment, r.Version.ToString(CultureInfo.InvariantCulture),
            summary.GetValueOrDefault(r.Attachment.Id)))];
    }

    /// <summary>Сводка строки списка анализа: глав и поставщиков. Поставщик — ИНН, без ИНН — имя; предложение без
    /// того и другого поставщиком не считается.</summary>
    private static async Task<Dictionary<Guid, (int Chapters, int? Suppliers)>> AnalysisSummaryAsync(
        PlanDbContext db, List<Guid> ids, CancellationToken ct)
    {
        var chapters = await db.ConjunctureItems.AsNoTracking().Where(i => ids.Contains(i.AnalysisId))
            .GroupBy(i => i.AnalysisId).Select(g => new { g.Key, Count = g.Select(i => i.ChapterIndex).Distinct().Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var suppliers = await (
                from o in db.ConjunctureOffers.AsNoTracking()
                join i in db.ConjunctureItems.AsNoTracking() on o.ItemId equals i.Id
                where ids.Contains(i.AnalysisId) && (o.SupplierInn != null || o.SupplierName != null)
                select new { i.AnalysisId, Key = o.SupplierInn ?? o.SupplierName })
            .Distinct().GroupBy(x => x.AnalysisId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return ids.ToDictionary(id => id, id => (chapters.GetValueOrDefault(id), (int?)suppliers.GetValueOrDefault(id)));
    }

    private static async Task<Dictionary<Guid, (int Chapters, int? Suppliers)>> StatementSummaryAsync(
        PlanDbContext db, List<Guid> ids, CancellationToken ct)
    {
        var chapters = await db.VolumeStatementLines.AsNoTracking().Where(l => ids.Contains(l.StatementId))
            .GroupBy(l => l.StatementId).Select(g => new { g.Key, Count = g.Select(l => l.ChapterIndex).Distinct().Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return ids.ToDictionary(id => id, id => (chapters.GetValueOrDefault(id), (int?)null));
    }

    public static async Task<EstimateAttachmentDto> OneAsync(PlanDbContext db, EstimateAttachment attachment, CancellationToken ct)
    {
        var list = attachment is ConjunctureAnalysis
            ? await ListAsync(db, db.ConjunctureAnalyses.Where(a => a.Id == attachment.Id), ct)
            : await ListAsync(db, db.VolumeStatements.Where(s => s.Id == attachment.Id), ct);
        return list.Count == 1 ? list[0] : throw NotFound(Kind(attachment));
    }

    public static string Location(EstimateAttachment attachment) => attachment is ConjunctureAnalysis
        ? $"/api/plan/conjuncture/{attachment.Id}/items"
        : $"/api/plan/volume-statements/{attachment.Id}/lines";

    public static async Task<ConjunctureItemsDto> ItemsAsync(
        PlanDbContext db, IModuleUser user, Guid analysisId, int? offset, int? limit, CancellationToken ct)
    {
        var (skip, take) = Page(offset, limit);
        var hidden = await EstimateViews.HiddenAsync(user, ct);
        var cost = hidden is null;
        var all = db.ConjunctureItems.AsNoTracking().Where(i => i.AnalysisId == analysisId);
        var total = await all.CountAsync(ct);
        var items = await all.OrderBy(i => i.Order).Skip(skip).Take(take)
            .Include(i => i.Offers.OrderBy(o => o.Order)).AsSplitQuery().ToListAsync(ct);
        return new ConjunctureItemsDto(total, skip, [.. items.Select(i => Item(i, cost))], hidden);
    }

    public static async Task<VolumeLinesDto> LinesAsync(PlanDbContext db, Guid statementId, int? offset, int? limit, CancellationToken ct)
    {
        var (skip, take) = Page(offset, limit);
        var all = db.VolumeStatementLines.AsNoTracking().Where(l => l.StatementId == statementId);
        var total = await all.CountAsync(ct);
        var lines = await all.OrderBy(l => l.Order).Skip(skip).Take(take).ToListAsync(ct);
        var parents = await ParentsAsync(all, lines, ct);
        return new VolumeLinesDto(total, skip,
        [
            .. lines.Select(l => new VolumeLineDto(l.Id, l.Order, l.ChapterIndex, l.ChapterCaption, l.Number, l.Caption,
                l.Code, l.Units, l.Quantity, l.Formula, l.Drawings, l.Note, l.IsSubordinate,
                parents.GetValueOrDefault(l.Id))),
        ]);
    }

    /// <summary>
    /// Вышестоящая работа подчинённых строк страницы: ближайшая не подчинённая выше в той же главе. Своей ссылки
    /// файл не пишет — <c>SlaveRow</c> идёт следом за своей работой. Первая строка страницы может быть подчинённой
    /// работе с прошлой страницы — её ищет один запрос до начала страницы.
    /// </summary>
    private static async Task<Dictionary<Guid, string>> ParentsAsync(
        IQueryable<VolumeStatementLine> all, List<VolumeStatementLine> page, CancellationToken ct)
    {
        var result = new Dictionary<Guid, string>();
        if (!page.Any(l => l.IsSubordinate)) return result;
        var first = page[0];
        var master = first.IsSubordinate
            ? await all.Where(l => l.Order < first.Order && l.ChapterIndex == first.ChapterIndex && !l.IsSubordinate)
                .OrderByDescending(l => l.Order).FirstOrDefaultAsync(ct)
            : null;
        foreach (var line in page)
        {
            if (master is not null && master.ChapterIndex != line.ChapterIndex) master = null;
            if (!line.IsSubordinate) master = line;
            else if (master is not null) result[line.Id] = master.Number;
        }
        return result;
    }

    public static NotFoundException NotFound(string kind) => new(kind == "ka"
        ? "Конъюнктурный анализ не найден — возможно, его удалили."
        : "Ведомость объёмов работ не найдена — возможно, её удалили.");

    private static (int Skip, int Take) Page(int? offset, int? limit)
    {
        if (offset is < 0) throw new InvalidRequestException("Смещение страницы (offset) не может быть меньше нуля.");
        return (offset ?? 0, limit is { } l ? Math.Clamp(l, 1, MaxLimit) : DefaultLimit);
    }

    private static string Kind(EstimateAttachment attachment) => attachment is ConjunctureAnalysis ? "ka" : "vor";

    private static EstimateAttachmentDto Dto(EstimateAttachment a, string version, (int Chapters, int? Suppliers) summary) =>
        new(a.Id, Kind(a), a.EstimateId, a.FileName, a.Number, a.Name, a.ProgramVersion, a.SchemaVersion, a.CreationDate,
            (a as ConjunctureAnalysis)?.CurrentPriceDate, a.ParsedPositions, a.PositionsInFile, summary.Chapters,
            summary.Suppliers, a.ImportedAt, a.ImportedByName, version);

    private static ConjunctureItemDto Item(ConjunctureItem i, bool cost) =>
        new(i.Id, i.Order, i.ChapterIndex, i.ChapterCaption, i.Number, i.Caption, i.Units, i.IsInactive,
            i.Selection.ToString().ToLowerInvariant(), i.SelectedOffer,
            [
                .. i.Offers.Select(o => new ConjunctureOfferDto(o.Order, o.Number, o.Caption, o.Units, o.ResourceCode,
                    o.SupplierName, o.SupplierInn, o.Vat, o.OfferDate, o.Order == i.SelectedOffer,
                    cost ? new ConjunctureOfferCostDto(o.Price, o.PriceWithVat) : null)),
            ],
            cost ? new ConjunctureItemCostDto(i.SelectedPrice) : null);
}
