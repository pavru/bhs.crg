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

public static class PriceListEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var prices = endpoints.MapGroup("/api/plan/pricelist").WithTags("Прайс-лист");
        var edit = AppPolicies.Permission(PlanModule.PriceListEdit);
        prices.MapGet("/", ListAsync).RequireAuthorization(AppPolicies.Permission(PlanModule.OfferRead));
        prices.MapPut("/{workTypeId:guid}", PutAsync).RequireAuthorization(edit);
        prices.MapDelete("/entries/{id:guid}", DeleteAsync).RequireAuthorization(edit);
    }

    private static async Task<IResult> ListAsync(
        DateOnly? date, PlanDbContext db, IModuleClock clock, IModuleWorkTypes workTypes, CancellationToken ct)
    {
        var on = date ?? await clock.TodayAsync(ct);
        var entries = await db.PriceListEntries.AsNoTracking()
            .OrderByDescending(e => e.ValidFrom)
            .Select(e => new { Entry = e, Version = EF.Property<uint>(e, PlanDbContext.RowVersion) })
            .ToListAsync(ct);
        var ids = entries.Select(e => e.Entry.WorkTypeId).Distinct().ToArray();
        IReadOnlyList<ModuleWorkType> works = ids.Length == 0 ? [] : await workTypes.GetAsync(ids, ct) ?? [];
        var names = works.ToDictionary(w => w.Id);
        var items = entries.GroupBy(e => e.Entry.WorkTypeId).Select(g =>
        {
            var work = names.GetValueOrDefault(g.Key);
            var effective = PriceListPricing.Effective(g.Select(e => e.Entry).Where(e => e.UnitId == work?.UnitId), on);
            var history = g.Select(e => View(e.Entry, e.Version.ToString(CultureInfo.InvariantCulture))).ToList();
            return new PriceListWorkDto(g.Key, work?.Code, work?.DisplayName, work?.UnitName,
                work?.UnitId, work?.Archived ?? false, history.FirstOrDefault(e => e.Id == effective?.Id), history);
        }).OrderBy(w => w.Code, StringComparer.Ordinal).ThenBy(w => w.Name).ThenBy(w => w.WorkTypeId).ToList();
        return Results.Ok(new PriceListDto(on, items));
    }

    private static async Task<IResult> PutAsync(
        Guid workTypeId, PriceListPutRequest body, HttpRequest request, PlanDbContext db,
        IModuleWorkTypes workTypes, IModuleUser user, IModuleClock clock, CancellationToken ct)
    {
        var date = body.ValidFrom ?? throw new InvalidRequestException("Укажите дату начала действия цены («validFrom»).");
        var entry = await db.PriceListEntries.SingleOrDefaultAsync(e => e.WorkTypeId == workTypeId && e.ValidFrom == date, ct);
        if (entry is null)
        {
            if (!SeenVersion.TryRead(request, out var seen)) throw new InvalidRequestException(SeenVersion.Malformed);
            if (seen is not null)
                throw new ConflictException("Строки прайс-листа на эту дату больше нет. Перечитайте прайс-лист.");
            var works = await workTypes.GetAsync([workTypeId], ct) ?? throw new ConflictException(IModuleWorkTypes.NotSetUp);
            var work = EnsureSelectable(works.SingleOrDefault(), workTypeId);
            entry = PriceListEntry.Create(workTypeId, work.UnitId!.Value, work.UnitName!, date);
            db.PriceListEntries.Add(entry);
        }
        else
        {
            EnsureSeen(request, db, entry);
        }
        var price = body.Price ?? throw new InvalidRequestException("Укажите цену за единицу («price»).");
        entry.SetPrice(price, user.Id, clock.UtcNow());
        await db.SaveChangesAsync(ct);
        return Results.Ok(View(entry, db.VersionOf(entry)));
    }

    private static async Task<IResult> DeleteAsync(Guid id, HttpRequest request, PlanDbContext db, CancellationToken ct)
    {
        var entry = await db.PriceListEntries.SingleOrDefaultAsync(e => e.Id == id, ct)
            ?? throw new NotFoundException("Строка прайс-листа не найдена.");
        EnsureSeen(request, db, entry);
        db.PriceListEntries.Remove(entry);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static void EnsureSeen(HttpRequest request, PlanDbContext db, PriceListEntry entry) =>
        EstimateEndpoints.EnsureSeen(request, db.VersionOf(entry), "Строку прайс-листа", "прайс-лист");

    internal static ModuleWorkType EnsureSelectable(ModuleWorkType? work, Guid id)
    {
        if (work is null) throw new InvalidRequestException($"Вид работы {id} не найден в классификаторе.");
        if (work.Archived) throw new InvalidRequestException($"Вид работы «{work.DisplayName}» в архиве.");
        if (work.UnitId is null || string.IsNullOrWhiteSpace(work.UnitName))
            throw new InvalidRequestException($"У вида работы «{work.DisplayName}» не задана единица классификатора.");
        return work;
    }

    private static PriceListEntryDto View(PriceListEntry entry, string version) =>
        new(entry.Id, entry.WorkTypeId, entry.UnitId, entry.Unit, entry.ValidFrom, entry.Price, entry.ChangedBy, entry.ChangedAt, version);
}

public sealed record PriceListPutRequest(DateOnly? ValidFrom, decimal? Price);
public sealed record PriceListEntryDto(
    Guid Id, Guid WorkTypeId, Guid UnitId, string Unit, DateOnly ValidFrom, decimal Price,
    Guid? ChangedBy, DateTimeOffset ChangedAt, string Version);
public sealed record PriceListWorkDto(
    Guid WorkTypeId, string? Code, string? Name, string? Unit, Guid? UnitId, bool Archived,
    PriceListEntryDto? Effective, IReadOnlyList<PriceListEntryDto> History);
public sealed record PriceListDto(DateOnly Date, IReadOnlyList<PriceListWorkDto> Items);
