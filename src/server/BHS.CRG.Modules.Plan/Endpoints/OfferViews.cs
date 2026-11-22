using System.Globalization;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Offers;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Endpoints;

/// <summary>
/// Ответы КП — одним местом на чтение и на правку (change <c>plan-13-offers</c>, ТЗ PLAN-10, PLAN-11).
///
/// <para><b>Деньги по правам</b> (D4 зонтика): цены продажи, наценки и суммы — всем, кому открыт адрес
/// (<c>plan.offer.read</c>). Себестоимость строки из сметы и маржа — только с <c>plan.estimate.cost</c>; без
/// права их нет в ответе вовсе, ни пустыми, ни нулём, а носитель <c>hidden</c> называет право.</para>
/// </summary>
internal static class OfferViews
{
    public static NotFoundException NotFound() => new("КП не найдено — возможно, его удалили.");

    /// <summary>Коды состояний для клиента — словарём: новое состояние без кода сразу краснит тест ответа.</summary>
    private static readonly IReadOnlyDictionary<OfferStatus, string> StatusCodes = new Dictionary<OfferStatus, string>
    {
        [OfferStatus.Draft] = "draft",
        [OfferStatus.Sent] = "sent",
        [OfferStatus.Accepted] = "accepted",
        [OfferStatus.Rejected] = "rejected",
        [OfferStatus.NoAnswer] = "no-answer",
    };

    public static string StatusCode(OfferStatus status) => StatusCodes[status];

    public static string VatCode(OfferVatMode mode) => mode switch
    {
        OfferVatMode.Included => "included",
        OfferVatMode.OnTop => "on-top",
        _ => "none",
    };

    public static OfferVatMode VatMode(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        null or "" or "none" => OfferVatMode.None,
        "included" => OfferVatMode.Included,
        "on-top" => OfferVatMode.OnTop,
        _ => throw new InvalidRequestException($"Режим НДС «{code}» неизвестен: ожидается included, on-top или none."),
    };

    private static string LevelCode(MarkupLevel level) => level switch
    {
        MarkupLevel.Line => "line",
        MarkupLevel.Section => "section",
        _ => "offer",
    };

    /// <summary>КП целиком: шапка, разделы, строки, деньги — и себестоимость по праву.</summary>
    public static async Task<OfferDto> OneAsync(
        PlanDbContext db, OfferDesk desk, IModuleUser user, Guid id, CancellationToken ct)
    {
        // Данные и версия — один снимок: новая версия рядом со старой шапкой пропустила бы чужую правку.
        var snapshot = await db.Offers.AsNoTracking().AsSingleQuery().Include(o => o.Lines).Include(o => o.SectionMarkups)
            .Where(o => o.Id == id)
            .Select(o => new { Offer = o, Version = EF.Property<uint>(o, PlanDbContext.RowVersion) })
            .FirstOrDefaultAsync(ct) ?? throw NotFound();
        var offer = snapshot.Offer;
        var version = snapshot.Version;

        var cost = await user.HasAsync(PlanModule.EstimateCost, ct);
        var bases = cost && offer.RevisionId is { } revision ? await desk.BaseTotalsAsync(revision, ct) : null;
        var customers = await desk.CustomersAsync([offer.CustomerId], ct);
        var revisions = await RevisionsAsync(db, [offer.RevisionId], ct);

        var sectionMarkups = offer.SectionMarkups.ToDictionary(s => s.SectionCode, s => s.Markup, StringComparer.Ordinal);
        var lines = offer.Lines.OrderBy(l => l.Order).Select(l =>
        {
            var markup = OfferPricing.Effective(l.Markup, l.LsrSectionCode, sectionMarkups, offer.Markup);
            decimal? baseTotal = bases is not null && l.RevisionRowId is { } row ? bases.GetValueOrDefault(row) : null;
            return new OfferLineDto(
                l.Id, l.Order, l.Number,
                l.LsrSectionCode is null && l.LsrSectionName is null ? null : new LsrSectionDto(l.LsrSectionCode, l.LsrSectionName),
                l.Name, l.Volume, l.Unit, l.WorkTypeId, l.Material ? "material" : "work", l.Markup,
                new OfferMarkupDto(markup.Value, LevelCode(markup.Level)), l.Price, Amount(l), Marks(l, bases),
                bases is null ? null : new OfferCostDto(baseTotal, baseTotal is { } b ? l.Amount - b : null), PriceSource(l));
        }).ToList();

        var sections = offer.Lines.Where(l => l.LsrSectionCode is not null)
            .GroupBy(l => l.LsrSectionCode!, StringComparer.Ordinal)
            .Select(g => new OfferSectionDto(g.Key, g.First().LsrSectionName, g.Count(),
                sectionMarkups.TryGetValue(g.Key, out var m) ? m : null))
            .OrderBy(s => offer.Lines.Where(l => l.LsrSectionCode == s.Code).Min(l => l.Order))
            .ToList();

        OfferCostDto? total = null;
        if (bases is not null)
        {
            var known = lines.Where(l => l.Cost?.Total is not null).ToList();
            var baseSum = known.Sum(l => l.Cost!.Total!.Value);
            total = new OfferCostDto(baseSum, known.Count == 0 ? null : known.Sum(l => l.Amount.GetValueOrDefault()) - baseSum);
        }

        return new OfferDto(
            offer.Id, offer.Number, offer.Date, StatusCode(offer.Status), offer.CurrentVersion, offer.SentOn, Text(version),
            offer.ConstructionId, Customer(offer.CustomerId, customers), offer.RevisionId is { } r ? revisions.GetValueOrDefault(r) : null,
            new OfferVatDto(VatCode(offer.VatMode), offer.VatRate), offer.Markup, sections,
            Money(offer), lines, OfferRequisites.Customer(offer),
            offer.CreatedAt, offer.CreatedByName, offer.ChangedAt, total,
            cost || offer.RevisionId is null ? null : [new HiddenPart("cost", PlanModule.EstimateCost, PlanModule.EstimateCostGives)])
        {
            PriceListDate = offer.PriceListDate,
        };
    }

    /// <summary>
    /// КП страницей — свежие первыми: по дате КП и номеру. Курсор — дата, год и порядковый номер последнего КП
    /// страницы: КП, заведённое между страницами, не сдвинет следующую.
    /// </summary>
    public static async Task<OfferListDto> PageAsync(
        PlanDbContext db, OfferDesk desk, Guid? constructionId, Guid? estimateId, string? cursor, int? limit,
        CancellationToken ct)
    {
        var take = limit is { } l ? Math.Clamp(l, 1, MaxPage) : DefaultPage;
        var query = db.Offers.AsNoTracking();
        if (constructionId is { } site) query = query.Where(o => o.ConstructionId == site);
        if (estimateId is { } estimate)
            query = query.Where(o => db.EstimateRevisions.Any(r => r.Id == o.RevisionId && r.EstimateId == estimate));
        if (!string.IsNullOrEmpty(cursor))
        {
            var (date, year, sequence) = Cursor(cursor);
            query = query.Where(o => o.Date < date || (o.Date == date && (o.Year < year || (o.Year == year && o.Sequence < sequence))));
        }

        var page = await query.OrderByDescending(o => o.Date).ThenByDescending(o => o.Year).ThenByDescending(o => o.Sequence)
            .Select(o => new { Offer = o, Version = EF.Property<uint>(o, PlanDbContext.RowVersion),
                Incomplete = o.RevisionId == null && o.Lines.Any(l => l.PriceListPrice == null) })
            .Take(take + 1).ToListAsync(ct);
        var more = page.Count > take;
        if (more) page.RemoveAt(page.Count - 1);

        var customers = await desk.CustomersAsync(page.Select(p => p.Offer.CustomerId), ct);
        var revisions = await RevisionsAsync(db, page.Select(p => p.Offer.RevisionId), ct);
        var items = page.Select(p => new OfferListItemDto(
            p.Offer.Id, p.Offer.Number, p.Offer.Date, StatusCode(p.Offer.Status), p.Offer.CurrentVersion, p.Offer.SentOn, Text(p.Version),
            p.Offer.ConstructionId, Customer(p.Offer.CustomerId, customers),
            p.Offer.RevisionId is { } r ? revisions.GetValueOrDefault(r) : null,
            new OfferVatDto(VatCode(p.Offer.VatMode), p.Offer.VatRate),
            new OfferMoneyDto(p.Offer.LinesTotal, p.Offer.VatAmount, p.Offer.Total, p.Incomplete), p.Offer.ChangedAt)).ToList();

        var last = page.Count > 0 ? page[^1].Offer : null;
        return new OfferListDto(items, more && last is not null
            ? $"{last.Date.DayNumber.ToString(CultureInfo.InvariantCulture)}_{last.Year.ToString(CultureInfo.InvariantCulture)}_{last.Sequence.ToString(CultureInfo.InvariantCulture)}"
            : null);
    }

    public const int DefaultPage = 50;
    public const int MaxPage = 200;

    /// <summary>Производные у редакций сметы, без денег и без чтения истории. Зовущий проверяет чтение КП.</summary>
    internal static async Task<ILookup<Guid, OfferSummaryDto>> ForRevisionsAsync(
        PlanDbContext db, IReadOnlyList<Guid> revisions, CancellationToken ct)
    {
        var offers = await db.Offers.AsNoTracking().Where(o => o.RevisionId != null && revisions.Contains(o.RevisionId.Value))
            .OrderByDescending(o => o.Date).ThenByDescending(o => o.Year).ThenByDescending(o => o.Sequence)
            .Select(o => new { o.RevisionId, o.Id, o.Number, o.CurrentVersion, o.Status, o.SentOn }).ToListAsync(ct);
        return offers.ToLookup(o => o.RevisionId!.Value,
            o => new OfferSummaryDto(o.Id, o.Number, o.CurrentVersion, StatusCode(o.Status), o.SentOn));
    }

    private static (DateOnly Date, int Year, int Sequence) Cursor(string cursor)
    {
        var parts = cursor.Split('_');
        return parts.Length == 3
               && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var day)
               && day >= DateOnly.MinValue.DayNumber && day <= DateOnly.MaxValue.DayNumber
               && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var year)
               && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
            ? (DateOnly.FromDayNumber(day), year, sequence)
            : throw new InvalidRequestException(
                "Курсор страницы записан не так: ожидается значение поля «next» из прежнего ответа.");
    }

    /// <summary>
    /// Пометки строки (D17 зонтика): «не сопоставлена» — как в смете (ТЗ PLAN-4); «нет итога в смете» — цена не
    /// посчитана, и сумма строки 0. Вторая называется только тому, кто видит себестоимость: без права она
    /// сказала бы о деньгах сметы.
    /// </summary>
    private static List<PlanMark> Marks(OfferLine line, IReadOnlyDictionary<Guid, decimal?>? bases)
    {
        var marks = new List<PlanMark>();
        if (line.MissingPrice) marks.Add(new("no-price", "Нет цены в прайс-листе"));
        if (line.Unmatched) marks.Add(new("unmatched", "Строка сметы не сопоставлена с видом работы"));
        if (bases is not null && line.RevisionRowId is { } row && bases.GetValueOrDefault(row) is null)
            marks.Add(new("no-base", "У строки сметы нет итога — цена продажи не посчитана"));
        return marks;
    }

    internal static decimal? Amount(OfferLine line) => line.MissingPrice ? null : line.Amount;

    internal static OfferPriceSourceDto? PriceSource(OfferLine line) => line.RevisionRowId is not null
        ? null
        : line.PriceListValidFrom is { } date ? new("pricelist", date) : new("none");

    internal static OfferMoneyDto Money(Offer offer) =>
        new(offer.LinesTotal, offer.VatAmount, offer.Total, offer.Lines.Any(l => l.MissingPrice));

    private static OfferCustomerDto? Customer(Guid? id, IReadOnlyDictionary<Guid, ModuleCatalogRef>? known)
    {
        if (id is not { } customer) return null;
        if (known?.GetValueOrDefault(customer) is { } found) return new(customer, found.DisplayName, found.Archived, Lost: false);
        // Справочник не назначен — назвать нечем, но и «потеряна» сказать нельзя: не знаем.
        return new(customer, null, Archived: false, Lost: known is not null);
    }

    private static async Task<Dictionary<Guid, OfferRevisionDto>> RevisionsAsync(
        PlanDbContext db, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var wanted = ids.OfType<Guid>().Distinct().ToList();
        if (wanted.Count == 0) return [];
        return await (
                from r in db.EstimateRevisions.AsNoTracking()
                join e in db.Estimates.AsNoTracking() on r.EstimateId equals e.Id
                where wanted.Contains(r.Id)
                select new OfferRevisionDto(r.Id, r.Number, e.Id, e.Name))
            .ToDictionaryAsync(r => r.Id, ct);
    }

    private static string Text(uint version) => version.ToString(CultureInfo.InvariantCulture);
}
