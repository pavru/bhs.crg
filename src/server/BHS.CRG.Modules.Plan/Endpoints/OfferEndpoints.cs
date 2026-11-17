using System.Globalization;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Offers;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Endpoints;

/// <summary>
/// Адреса коммерческих предложений (change <c>plan-13-offers</c>, ТЗ PLAN-10, PLAN-11, PLAN-Q8).
///
/// <para><b>Ворота.</b> Группа модуля закрыта воротами <c>module:plan</c>; права — поимённо: чтение —
/// <c>plan.offer.read</c>, правка — <c>plan.offer.edit</c> (ТЗ AUTH-8, AUTH-9). «Сметчик» прав на КП не имеет
/// и цен продажи не видит (ТЗ PLAN-2).</para>
///
/// <para><b>Версия.</b> Каждая правка существующего КП называет версию заголовком <c>If-Match</c>: без него —
/// 400, с устаревшей — 409. Создание версии не называет — КП ещё нет, а редакцию оно лишь фиксирует.</para>
///
/// <para><b>Деньги считает код</b> (<see cref="OfferPricing" />): цены, суммы, НДС и «Итого» адреса не
/// принимают вовсе, а системные поля типа в данных заказчика — отказ охраны записи.</para>
/// </summary>
public static class OfferEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var offers = endpoints.MapGroup("/api/plan/offers").WithTags("Коммерческие предложения");
        var read = AppPolicies.Permission(PlanModule.OfferRead);
        var edit = AppPolicies.Permission(PlanModule.OfferEdit);

        offers.MapGet("/", ListAsync).RequireAuthorization(read);
        offers.MapGet("/{id:guid}", GetAsync).RequireAuthorization(read);
        offers.MapPost("/", CreateAsync).RequireAuthorization(edit);
        offers.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(edit);
        offers.MapPut("/{id:guid}/markups", MarkupsAsync).RequireAuthorization(edit);
        offers.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization(edit);
    }

    private static async Task<IResult> ListAsync(
        Guid? constructionId, Guid? estimateId, string? cursor, int? limit, PlanDbContext db, OfferDesk desk,
        CancellationToken ct) =>
        Results.Ok(await OfferViews.PageAsync(db, desk, constructionId, estimateId, cursor, limit, ct));

    private static async Task<IResult> GetAsync(
        Guid id, PlanDbContext db, OfferDesk desk, IModuleUser user, CancellationToken ct) =>
        Results.Ok(await OfferViews.OneAsync(db, desk, user, id, ct));

    /// <summary>
    /// «Сделать КП из редакции» — одно действие (ТЗ PLAN-10): строки редакции переносятся все, с
    /// несопоставленными (ТЗ PLAN-4), цены считает наценка предложения, а редакция фиксируется этим КП в той же
    /// записи (ТЗ PLAN-5): КП без фиксации или фиксация без КП были бы состоянием, которого не было.
    /// </summary>
    private static async Task<IResult> CreateAsync(
        OfferCreateRequest body, PlanDbContext db, OfferDesk desk, IModuleUser user, IModuleTypes types,
        IModuleWriteGuard guard, IModuleClock clock, IModuleActivityLog log, CancellationToken ct)
    {
        var typeId = await types.FindAsync(PlanRecordTypes.OfferCode, ct)
            ?? throw new ConflictException(
                $"Тип «{PlanRecordTypes.Offer.Name}» на экземпляре не заведён — поля заказчика КП не с чем сверить. Он " +
                "заводится при старте приложения с модулем «Планирование»; перезапустите приложение и повторите.");
        var revision = await db.EstimateRevisions.FirstOrDefaultAsync(r => r.Id == body.RevisionId, ct)
            ?? throw new InvalidRequestException("Редакции нет — её удалили либо адрес неверен.");
        var estimate = await db.Estimates.AsNoTracking().FirstAsync(e => e.Id == revision.EstimateId, ct);

        var markup = OfferPricing.CheckedMarkup(body.Markup ?? 0m, "предложения");
        var vatMode = OfferViews.VatMode(body.VatMode);
        var vatRate = OfferPricing.CheckedVat(vatMode, body.VatRate);
        await desk.EnsureCustomerAsync(body.CustomerId, stored: null, ct);

        var now = clock.UtcNow();
        var date = body.Date ?? await clock.TodayAsync(ct);
        var offer = Offer.Create(typeId, estimate.ConstructionId, revision.Id, date,
            await desk.NextSequenceAsync(date.Year, ct), user.Id, user.Name, now);
        offer.ReplaceHeader(date, body.CustomerId, vatMode, vatRate, now);
        offer.ChangeMarkup(markup, now);

        // Поля заказчика у создания — впервые: лежащего нет, и названное системное поле охрана отвергнет.
        var (guarded, customer) = OfferRequisites.Incoming(body.Data, stored: null);
        await OfferRequisites.EnsureAllowedAsync(guard, typeId, stored: null, guarded, ct);
        offer.ApplyCustomerData(customer, now);

        var rows = await desk.RevisionRowsAsync(revision.Id, ct);
        foreach (var row in rows) offer.Lines.Add(OfferLine.FromRevisionRow(offer.Id, row));
        OfferPricing.Reprice(offer, rows.ToDictionary(r => r.Id, r => r.Total));

        revision.Freeze(new EstimateFreeze("offer", offer.Label), now);
        db.Offers.Add(offer);
        await db.SaveChangesAsync(ct);

        await log.RecordAsync(OfferActions.Created, offer.Id.ToString(), offer.Label,
            after: $"смета «{estimate.Name}», редакция {revision.Number}; строк {offer.Lines.Count}; итого {Money(offer.Total)}",
            ct: ct);
        return Results.Created($"/api/plan/offers/{offer.Id}", await OfferViews.OneAsync(db, desk, user, offer.Id, ct));
    }

    /// <summary>
    /// Шапка КП целиком: дата, заказчик, режим и ставка НДС, поля заказчика. Номер не меняется и с датой: он
    /// выдан при создании (D19 зонтика). Суммы пересчитываются — НДС зависит от режима и ставки.
    /// </summary>
    private static async Task<IResult> UpdateAsync(
        Guid id, OfferUpdateRequest body, HttpRequest request, PlanDbContext db, OfferDesk desk, IModuleUser user,
        IModuleWriteGuard guard, IModuleClock clock, IModuleActivityLog log, CancellationToken ct)
    {
        var offer = await LoadAsync(db, id, ct);
        EstimateEndpoints.EnsureSeen(request, db.VersionOf(offer), "КП", "КП");

        var date = body.Date ?? throw new InvalidRequestException("Не названа дата КП — шапка присылается целиком.");
        if (string.IsNullOrWhiteSpace(body.VatMode))
            throw new InvalidRequestException(
                "Не назван режим НДС («vatMode»: included, on-top или none) — шапка присылается целиком.");
        var vatMode = OfferViews.VatMode(body.VatMode);
        var vatRate = OfferPricing.CheckedVat(vatMode, body.VatRate);
        await desk.EnsureCustomerAsync(body.CustomerId, offer.CustomerId, ct);

        var before = Header(offer);
        var now = clock.UtcNow();
        var (guarded, customer) = OfferRequisites.Incoming(body.Data, offer);
        await OfferRequisites.EnsureAllowedAsync(guard, offer.DocumentTypeId, OfferRequisites.Stored(offer), guarded, ct);
        offer.ApplyCustomerData(customer, now);
        offer.ReplaceHeader(date, body.CustomerId, vatMode, vatRate, now);
        OfferPricing.Retotal(offer);
        await db.SaveChangesAsync(ct);

        await log.RecordAsync(OfferActions.Changed, offer.Id.ToString(), offer.Label, before, Header(offer), ct);
        return Results.Ok(await OfferViews.OneAsync(db, desk, user, id, ct));
    }

    /// <summary>
    /// Наценки КП целиком (ТЗ PLAN-11): предложения, разделов ЛСР и строк. Цены пересчитываются по базам
    /// редакции — она зафиксирована, и база не плывёт.
    /// </summary>
    private static async Task<IResult> MarkupsAsync(
        Guid id, OfferMarkupsRequest body, HttpRequest request, PlanDbContext db, OfferDesk desk, IModuleUser user,
        IModuleClock clock, IModuleActivityLog log, CancellationToken ct)
    {
        var offer = await LoadAsync(db, id, ct);
        EstimateEndpoints.EnsureSeen(request, db.VersionOf(offer), "КП", "КП");

        var markup = OfferPricing.CheckedMarkup(body.Markup, "предложения");
        var sections = SectionMarkups(offer, body.Sections ?? []);
        var lines = LineMarkups(offer, body.Lines ?? []);

        var before = Markups(offer);
        offer.ChangeMarkup(markup, clock.UtcNow());
        foreach (var stale in offer.SectionMarkups.Where(s => !sections.ContainsKey(s.SectionCode)).ToList())
        {
            offer.SectionMarkups.Remove(stale);
            db.OfferSectionMarkups.Remove(stale);
        }
        foreach (var (code, value) in sections)
        {
            if (offer.SectionMarkups.FirstOrDefault(s => s.SectionCode == code) is { } existing) existing.Change(value);
            else offer.SectionMarkups.Add(OfferSectionMarkup.Create(offer.Id, code, value));
        }
        foreach (var line in offer.Lines) line.ChangeMarkup(lines.TryGetValue(line.Id, out var own) ? own : null);

        var bases = offer.RevisionId is { } revision ? await desk.BaseTotalsAsync(revision, ct) : [];
        OfferPricing.Reprice(offer, bases);
        await db.SaveChangesAsync(ct);

        await log.RecordAsync(OfferActions.Markups, offer.Id.ToString(), offer.Label, before, Markups(offer), ct);
        return Results.Ok(await OfferViews.OneAsync(db, desk, user, id, ct));
    }

    /// <summary>
    /// Удалить КП. Фиксацию редакции удаление не снимает: из редакции делали производную, и смета об этом
    /// помнит (D3 зонтика).
    /// </summary>
    private static async Task<IResult> DeleteAsync(
        Guid id, HttpRequest request, PlanDbContext db, IModuleActivityLog log, CancellationToken ct)
    {
        var offer = await db.Offers.FirstOrDefaultAsync(o => o.Id == id, ct) ?? throw OfferViews.NotFound();
        EstimateEndpoints.EnsureSeen(request, db.VersionOf(offer), "КП", "КП");

        db.Offers.Remove(offer);
        await db.SaveChangesAsync(ct);
        await log.RecordAsync(OfferActions.Deleted, id.ToString(), offer.Label, before: $"итого {Money(offer.Total)}", ct: ct);
        return Results.NoContent();
    }

    private static async Task<Offer> LoadAsync(PlanDbContext db, Guid id, CancellationToken ct) =>
        await db.Offers.Include(o => o.Lines).Include(o => o.SectionMarkups).FirstOrDefaultAsync(o => o.Id == id, ct)
        ?? throw OfferViews.NotFound();

    /// <summary>Наценки разделов: раздел обязан быть среди строк КП и называться один раз.</summary>
    private static Dictionary<string, decimal> SectionMarkups(Offer offer, IReadOnlyList<OfferSectionMarkupRequest> sent)
    {
        var known = offer.Lines.Select(l => l.LsrSectionCode).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var s in sent)
        {
            var code = s.Code?.Trim() ?? string.Empty;
            if (!known.Contains(code))
                throw new InvalidRequestException($"Раздела ЛСР «{code}» среди строк КП нет — наценку ставить некуда.");
            if (!result.TryAdd(code, OfferPricing.CheckedMarkup(s.Markup, $"раздела «{code}»")))
                throw new InvalidRequestException($"Раздел ЛСР «{code}» назван дважды — какая наценка действует, не понять.");
        }
        return result;
    }

    /// <summary>Свои наценки строк: строка обязана быть этого КП и называться один раз.</summary>
    private static Dictionary<Guid, decimal> LineMarkups(Offer offer, IReadOnlyList<OfferLineMarkupRequest> sent)
    {
        var lines = offer.Lines.ToDictionary(l => l.Id);
        var result = new Dictionary<Guid, decimal>();
        foreach (var l in sent)
        {
            if (!lines.TryGetValue(l.LineId, out var line))
                throw new InvalidRequestException("Строки нет в этом КП — наценку ставить некуда. Перечитайте КП.");
            if (!result.TryAdd(l.LineId, OfferPricing.CheckedMarkup(l.Markup, $"строки «{line.Name}»")))
                throw new InvalidRequestException($"Строка «{line.Name}» названа дважды — какая наценка действует, не понять.");
        }
        return result;
    }

    private static string Header(Offer offer) =>
        $"дата {offer.Date:dd.MM.yyyy}; НДС {OfferViews.VatCode(offer.VatMode)}" +
        (offer.VatRate is { } rate ? $" {rate.ToString(CultureInfo.InvariantCulture)} %" : "") + $"; итого {Money(offer.Total)}";

    private static string Markups(Offer offer) =>
        $"наценка {offer.Markup.ToString(CultureInfo.InvariantCulture)} %; разделов со своей: {offer.SectionMarkups.Count}; " +
        $"строк со своей: {offer.Lines.Count(l => l.Markup is not null)}; итого {Money(offer.Total)}";

    private static string Money(decimal value) => value.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"));
}
