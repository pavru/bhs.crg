namespace BHS.CRG.Modules.Plan.Data;

/// <summary>
/// Коммерческое предложение позиции конъюнктурного анализа (change <c>plan-11-conjuncture</c>): поставщик — названием
/// и ИНН из файла, цена и дата предложения. Цены — деньги сметы: только с <c>plan.estimate.cost</c> (D4 зонтика).
/// </summary>
public sealed class ConjunctureOffer
{
    public const int CodeLength = 100;
    public const int InnLength = 20;
    public const int UnitLength = 100;

    private ConjunctureOffer() { }

    public Guid Id { get; private set; }

    public Guid ItemId { get; private set; }

    /// <summary>Порядок по файлу, с единицы, — по нему позиция называет выбранное предложение.</summary>
    public int Order { get; private set; }

    public string Number { get; private set; } = string.Empty;

    public string Caption { get; private set; } = string.Empty;

    public string? Units { get; private set; }

    /// <summary>Код группы ресурса по КСР (<c>KSRGroup</c>) — у позиции анализа своего кода в файле нет.</summary>
    public string? ResourceCode { get; private set; }

    public string? SupplierName { get; private set; }

    public string? SupplierInn { get; private set; }

    /// <summary>Отпускная цена без НДС (<c>OptPrice</c>).</summary>
    public decimal? Price { get; private set; }

    /// <summary>Ставка НДС, % — не деньги: видна и без права на стоимость.</summary>
    public decimal? Vat { get; private set; }

    public decimal? PriceWithVat { get; private set; }

    /// <summary>Дата предложения.</summary>
    public DateOnly? OfferDate { get; private set; }

    internal static ConjunctureOffer From(Guid itemId, int order, GrandSmeta.ConjunctureOffer offer) => new()
    {
        Id = Guid.NewGuid(),
        ItemId = itemId,
        Order = order,
        Number = PlanText.Clip(offer.Number, ConjunctureItem.NumberLength) ?? string.Empty,
        Caption = offer.Caption,
        Units = PlanText.Clip(offer.Units, UnitLength),
        ResourceCode = PlanText.Clip(offer.ResourceGroup, CodeLength),
        SupplierName = offer.Supplier?.Caption,
        SupplierInn = PlanText.Clip(offer.Supplier?.Inn, InnLength),
        Price = offer.SellingPrice,
        Vat = offer.Vat,
        PriceWithVat = offer.SellingPriceWithVat,
        OfferDate = offer.SellingDate,
    };
}
