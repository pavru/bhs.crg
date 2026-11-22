using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Offers;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Http;

namespace BHS.CRG.Modules.Plan.Endpoints;

public static partial class OfferEndpoints
{
    private static async Task<Offer> CreateDraftAsync(
        Guid constructionId, Guid? revisionId, OfferDraftHeader header, OfferDesk desk, IModuleUser user,
        IModuleTypes types, IModuleWriteGuard guard, IModuleClock clock, CancellationToken ct)
    {
        var typeId = await types.FindAsync(PlanRecordTypes.OfferCode, ct)
            ?? throw new ConflictException(
                $"Тип «{PlanRecordTypes.Offer.Name}» на экземпляре не заведён. " +
                "Перезапустите приложение с модулем «Планирование» и повторите.");
        var markup = OfferPricing.CheckedMarkup(header.Markup ?? 0m, "предложения");
        var vatMode = OfferViews.VatMode(header.VatMode);
        var vatRate = OfferPricing.CheckedVat(vatMode, header.VatRate);
        await desk.EnsureCustomerAsync(header.CustomerId, stored: null, ct);

        var now = clock.UtcNow();
        var date = header.Date ?? await clock.TodayAsync(ct);
        var offer = Offer.Create(typeId, constructionId, revisionId, date,
            await desk.NextSequenceAsync(date.Year, ct), user.Id, user.Name, now);
        offer.ReplaceHeader(date, header.CustomerId, vatMode, vatRate, now);
        offer.ChangeMarkup(markup, now);
        var (guarded, customer) = OfferRequisites.Incoming(header.Data, stored: null);
        await OfferRequisites.EnsureAllowedAsync(guard, typeId, stored: null, guarded, ct);
        offer.ApplyCustomerData(customer, now);
        return offer;
    }

    private static async Task<IResult> CreateFromPriceListAsync(
        OfferPriceListCreateRequest body, PlanDbContext db, OfferDesk desk, IModuleUser user, IModuleTypes types,
        IModuleWriteGuard guard, IModuleClock clock, IModuleActivityLog log, IModuleConstructions constructions,
        IModuleWorkTypes workTypes, CancellationToken ct)
    {
        if (body.Lines is not { Count: > 0 } lines)
            throw new InvalidRequestException("Добавьте хотя бы один вид работы в КП («lines»).");
        if (!(await constructions.ListAsync(ct)).Any(c => c.Id == body.ConstructionId))
            throw new InvalidRequestException("Стройка не найдена — её удалили либо адрес неверен.");

        var ids = lines.Select(l => l.WorkTypeId).Distinct().ToArray();
        var works = (await workTypes.GetAsync(ids, ct) ?? throw new ConflictException(IModuleWorkTypes.NotSetUp))
            .ToDictionary(w => w.Id);
        var offer = await CreateDraftAsync(body.ConstructionId, null, body.Header, desk, user, types, guard, clock, ct);
        var prices = await PriceListPricing.ForWorksAsync(db, ids, offer.Date, ct);
        foreach (var line in lines)
        {
            var work = PriceListEndpoints.EnsureSelectable(works.GetValueOrDefault(line.WorkTypeId), line.WorkTypeId);
            if (line.Volume <= 0m || line.Volume >= 1_000_000_000_000m || decimal.Round(line.Volume, 6) != line.Volume)
                throw new InvalidRequestException($"Объём «{work.DisplayName}» должен быть больше нуля, до 12 цифр до запятой и до шести после.");
            var added = OfferLine.FromWorkType(offer.Id, offer.Lines.Count + 1, work, line.Volume);
            added.SetPriceListEntry(prices.GetValueOrDefault(work.Id));
            offer.Lines.Add(added);
        }
        offer.SetPriceListDate(offer.Date, offer.CreatedAt);
        OfferPricing.Reprice(offer, baseTotals: new Dictionary<Guid, decimal?>());
        db.Offers.Add(offer);
        await db.SaveChangesAsync(ct);

        await log.RecordAsync(OfferActions.CreatedFromPriceList, offer.Id.ToString(), offer.Label,
            after: $"цены на {offer.Date:dd.MM.yyyy}; строк {offer.Lines.Count}; итого {Money(offer.Total)}", ct: ct);
        return Results.Created($"/api/plan/offers/{offer.Id}", await OfferViews.OneAsync(db, desk, user, offer.Id, ct));
    }

    private static async Task<IResult> RepriceFromPriceListAsync(
        Guid id, OfferPriceListRepriceRequest body, HttpRequest request, PlanDbContext db, OfferDesk desk,
        IModuleUser user, IModuleClock clock, IModuleActivityLog log, CancellationToken ct)
    {
        var offer = await LoadAsync(db, id, ct);
        var version = db.VersionOf(offer);
        EstimateEndpoints.EnsureSeen(request, version, "КП", "КП");
        offer.EnsureDraft();
        if (offer.RevisionId is not null)
            throw new ConflictException("По прайс-листу пересчитывается только КП, заведённое по прайс-листу.");

        var date = body.Date ?? offer.Date;
        var ids = offer.Lines.Select(l => l.WorkTypeId!.Value).Distinct().ToArray();
        var entries = await PriceListPricing.ForWorksAsync(db, ids, date, ct);
        var before = offer.Lines.ToDictionary(l => l.Id, l => (l.PriceListPrice, l.Price, Amount: OfferViews.Amount(l),
            Source: OfferViews.PriceSource(l)!));
        foreach (var line in offer.Lines)
            line.SetPriceListEntry(entries.GetValueOrDefault(line.WorkTypeId!.Value));
        OfferPricing.Reprice(offer, baseTotals: new Dictionary<Guid, decimal?>());
        var changes = offer.Lines.OrderBy(l => l.Order)
            .Where(l => before[l.Id] != (l.PriceListPrice, l.Price, OfferViews.Amount(l), OfferViews.PriceSource(l)!))
            .Select(l => new OfferPriceListLineChange(l.Id, l.WorkTypeId!.Value, l.Name, before[l.Id].Price, l.Price,
                before[l.Id].Amount, OfferViews.Amount(l), before[l.Id].Source, OfferViews.PriceSource(l)!)).ToList();
        var money = OfferViews.Money(offer);
        if (body.Preview) return Results.Ok(new OfferPriceListRepriceDto(date, version, changes, money, null));

        if (changes.Count > 0 || offer.PriceListDate != date)
        {
            offer.SetPriceListDate(date, clock.UtcNow());
            // Версия шапки должна участвовать в записи, даже когда часы вернули прежний момент.
            db.Entry(offer).Property(o => o.ChangedAt).IsModified = true;
            await db.SaveChangesAsync(ct);
            await log.RecordAsync(OfferActions.RepricedFromPriceList, id.ToString(), offer.Label,
                after: $"цены на {date:dd.MM.yyyy}; изменено строк: {changes.Count}; итого {Money(offer.Total)}", ct: ct);
        }
        var updated = await OfferViews.OneAsync(db, desk, user, id, ct);
        return Results.Ok(new OfferPriceListRepriceDto(date, updated.Version, changes, money, updated));
    }
}
