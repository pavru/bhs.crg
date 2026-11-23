using System.Text.Json;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Endpoints;
using BHS.CRG.Modules.Plan.Offers;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Printing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Printing;

public sealed class OfferPrintContext(PlanDbContext db, OfferDesk desk, IModuleConstructions constructions,
    IModulePrintCatalog catalog) : IModulePrintContext
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly string[] InternalKeys =
        [OfferRequisites.MarkupKey, "Себестоимость", "Маржа", "markup", "cost", "margin"];

    internal static readonly ModulePrintRequisites Requisites = new(
        [
            new(["number"], "Номер КП", "string"),
            new(["date"], "Дата КП", "date"),
            new(["version"], "Номер версии", "number"),
            new(["versionDate"], "Дата версии", "date"),
            new(["sentOn"], "Дата отправки", "date"),
            new(["construction"], "Стройка", "string"),
            new(["customer"], "Заказчик", "string"),
            new(["vat", "mode"], "Режим НДС", "string"),
            new(["vat", "rate"], "Ставка НДС", "number"),
            new(["money", "linesTotal"], "Сумма строк", "number"),
            new(["money", "vatAmount"], "Сумма НДС", "number"),
            new(["money", "total"], "Итого к оплате", "number"),
        ], "conditions", InternalKeys, [OfferRequisites.TotalKey]);

    public async Task<ModulePrintResult> BuildAsync(Guid recordId, IModuleUser user, CancellationToken ct)
    {
        if (!await user.HasAsync(PlanModule.OfferRead, ct)) return new ModulePrintResult.Forbidden();

        var offer = await db.Offers.AsNoTracking().AsSingleQuery().Include(o => o.Lines)
            .SingleOrDefaultAsync(o => o.Id == recordId, ct);
        if (offer is null) return new ModulePrintResult.Missing();

        var construction = (await constructions.ListAsync(ct)).FirstOrDefault(c => c.Id == offer.ConstructionId);
        var customers = await desk.CustomersAsync([offer.CustomerId], ct);
        var customer = offer.CustomerId is { } id ? customers?.GetValueOrDefault(id) : null;
        var conditions = await catalog.ResolveAsync(OfferRequisites.Customer(offer), ct);

        return new ModulePrintResult.Found(JsonSerializer.SerializeToElement(new
        {
            offer.Number,
            offer.Date,
            version = offer.CurrentVersion,
            versionDate = offer.SentOn ?? offer.Date,
            offer.SentOn,
            construction = construction?.Name,
            customer = customer?.DisplayName,
            lines = offer.Lines.OrderBy(l => l.Order).ThenBy(l => l.Id).Select(l => new
            {
                l.Number, l.Name, l.Volume, l.Unit, l.Price, Amount = OfferViews.Amount(l), Section = l.LsrSectionName,
            }),
            vat = new { mode = OfferViews.VatCode(offer.VatMode), rate = offer.VatRate },
            money = new { offer.LinesTotal, offer.VatAmount, offer.Total },
            conditions = CustomerFields(conditions, root: true),
        }, Json));
    }

    private static object CustomerFields(JsonElement value, bool root) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Where(p =>
                !InternalKeys.Contains(p.Name, StringComparer.OrdinalIgnoreCase)
                && !(root && string.Equals(p.Name, OfferRequisites.TotalKey, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(p => p.Name, p => CustomerFields(p.Value, root: false)),
        JsonValueKind.Array => value.EnumerateArray().Select(item => CustomerFields(item, root: false)).ToArray(),
        _ => value,
    };
}
