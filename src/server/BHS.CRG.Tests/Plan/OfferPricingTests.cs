using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Offers;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Расчёт денег КП (change <c>plan-13-offers</c>, ТЗ PLAN-11, решения В-8 и В-9): уровни наценки, база, округление,
/// НДС. Без базы и хоста — правило одно, и оно в <see cref="OfferPricing" />.
/// </summary>
public class OfferPricingTests
{
    private static readonly Dictionary<string, decimal> Sections = new(StringComparer.Ordinal) { ["1"] = 15m };

    /// <summary>20 % у КП, 15 % у раздела, 10 % у строки — действует самая частная (сценарий спеки).</summary>
    [Fact]
    public void Действует_самая_частная_наценка()
    {
        Assert.Equal(new EffectiveMarkup(10m, MarkupLevel.Line), OfferPricing.Effective(10m, "1", Sections, 20m));
        Assert.Equal(new EffectiveMarkup(15m, MarkupLevel.Section), OfferPricing.Effective(null, "1", Sections, 20m));
        Assert.Equal(new EffectiveMarkup(20m, MarkupLevel.Offer), OfferPricing.Effective(null, "2", Sections, 20m));
        Assert.Equal(new EffectiveMarkup(20m, MarkupLevel.Offer), OfferPricing.Effective(null, null, Sections, 20m));
        // Своя наценка 0 — тоже своя: «без наценки на эту строку» не равно «наценки не задано».
        Assert.Equal(new EffectiveMarkup(0m, MarkupLevel.Line), OfferPricing.Effective(0m, "1", Sections, 20m));
    }

    /// <summary>База (В-8) — итог строки ЛСР на её объём; цена — до копейки за единицу, сумма — цена × объём.</summary>
    [Fact]
    public void Цена_за_единицу_от_базы_до_копейки_сумма_от_цены()
    {
        // 1000 / 3 = 333,333…; × 1,2 = 400,00 — цена до копейки, сумма — цена × объём.
        Assert.Equal(new LinePrice(400.00m, 1200.00m), OfferPricing.Price(1000m, 3m, 20m));
        // 100 / 3 × 1,1 = 36,666… → 36,67; × 3 = 110,01 — сумма от округлённой цены, а не от итога.
        Assert.Equal(new LinePrice(36.67m, 110.01m), OfferPricing.Price(100m, 3m, 10m));
        // Половина копейки — от нуля: 0,125 → 0,13.
        Assert.Equal(new LinePrice(0.13m, 0.13m), OfferPricing.Price(0.125m, 1m, 0m));
    }

    [Fact]
    public void Строка_без_объёма_считается_от_итога_без_цены_а_без_итога_нулём()
    {
        Assert.Equal(new LinePrice(null, 1150.00m), OfferPricing.Price(1000m, null, 15m));
        Assert.Equal(new LinePrice(null, 1150.00m), OfferPricing.Price(1000m, 0m, 15m));
        Assert.Equal(new LinePrice(null, 0m), OfferPricing.Price(null, 5m, 15m));
    }

    /// <summary>НДС — с суммы строк, а не по строкам (В-9, как у счёта): сценарии спеки.</summary>
    [Fact]
    public void НДС_считается_с_суммы_строк()
    {
        Assert.Equal(new OfferTotals(1000.00m, 200.00m, 1200.00m), OfferPricing.Totals([600m, 400m], OfferVatMode.OnTop, 20m));
        Assert.Equal(new OfferTotals(1200.00m, 200.00m, 1200.00m), OfferPricing.Totals([1200m], OfferVatMode.Included, 20m));
        Assert.Equal(new OfferTotals(1000m, null, 1000m), OfferPricing.Totals([600m, 400m], OfferVatMode.None, null));
        // По строкам вышло бы 0,17 + 0,17 = 0,34; с суммы — 0,33: правило «с итога» видно на копейке.
        Assert.Equal(0.33m, OfferPricing.Totals([0.83m, 0.83m], OfferVatMode.OnTop, 20m).Vat);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1000.01)]
    [InlineData(12.345)]
    public void Наценка_вне_правил_отказ(double markup) =>
        Assert.Throws<InvalidRequestException>(() => OfferPricing.CheckedMarkup((decimal)markup, "предложения"));

    [Fact]
    public void Ставка_НДС_нужна_только_режиму_с_НДС()
    {
        Assert.Null(OfferPricing.CheckedVat(OfferVatMode.None, null));
        Assert.Equal(22m, OfferPricing.CheckedVat(OfferVatMode.OnTop, 22m));
        Assert.Throws<InvalidRequestException>(() => OfferPricing.CheckedVat(OfferVatMode.None, 20m));
        Assert.Throws<InvalidRequestException>(() => OfferPricing.CheckedVat(OfferVatMode.Included, null));
        Assert.Throws<InvalidRequestException>(() => OfferPricing.CheckedVat(OfferVatMode.OnTop, 0m));
    }
}
