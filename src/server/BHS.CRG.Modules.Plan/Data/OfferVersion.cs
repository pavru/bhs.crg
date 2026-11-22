using System.Text.Json;
using System.Text.Json.Serialization;
using BHS.CRG.Modules.Data;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Data;

/// <summary>Отправленная версия КП до открытия следующего черновика (PLAN-12, CORE-36). Только дописывается.</summary>
public sealed class OfferVersion : ModuleRecordVersion
{
    private OfferVersion() { }

    private OfferVersion(Offer offer, string content, Guid? author)
        : base(offer.Id, offer.CurrentVersion, content, string.Empty, author) { }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    /// <summary>Снимок колонок и данных КП; себестоимость из редакции в него не читается.</summary>
    public static OfferVersion Capture(Offer offer, Guid? author, string authorName) => new(offer,
        JsonSerializer.Serialize(new
        {
            offer.Number, offer.Date, offer.DocumentTypeId, offer.ConstructionId, offer.CustomerId,
            offer.RevisionId, source = offer.RevisionId is null ? "pricelist" : "estimate",
            offer.PriceListDate, offer.Status, offer.CurrentVersion, offer.SentOn, offer.Markup,
            vat = new { mode = offer.VatMode, rate = offer.VatRate },
            money = Endpoints.OfferViews.Money(offer),
            data = JsonSerializer.Deserialize<JsonElement>(offer.Data),
            offer.CreatedAt, offer.CreatedBy, offer.CreatedByName, offer.ChangedAt, authorName,
            sections = offer.SectionMarkups.OrderBy(s => s.SectionCode).Select(s => new { s.SectionCode, s.Markup }),
            lines = offer.Lines.OrderBy(l => l.Order).Select(l => new
            {
                l.Id, l.Order, l.RevisionRowId, l.WorkTypeId, l.Material, l.LsrSectionCode, l.LsrSectionName,
                l.Number, l.Name, l.Volume, l.Unit, l.Markup, l.Price,
                amount = Endpoints.OfferViews.Amount(l), priceSource = Endpoints.OfferViews.PriceSource(l),
            }),
        }, Json), author);

    internal static void Map(ModelBuilder builder) => Map<OfferVersion, Offer>(builder, "offer_versions");
}
