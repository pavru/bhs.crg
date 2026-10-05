using BHS.CRG.Modules.Costs.Data;

namespace BHS.CRG.Tests.Costs;

/// <summary>
/// «Затраты по стройке» — арифметика отчёта (задача G5, issue #1098, ТЗ COST-20): что входит в период,
/// как делится по стройкам и контрагентам, что значит «без НДС» там, где НДС вычесть нечем.
/// </summary>
public class SiteCostsTests
{
    private static readonly Guid SiteA = Guid.NewGuid();
    private static readonly Guid SiteB = Guid.NewGuid();
    private static readonly Guid Stock = Guid.NewGuid();
    private static readonly Guid Supplier = Guid.NewGuid();

    private static readonly Guid Cable = Guid.NewGuid();
    private static readonly Guid Delivery = Guid.NewGuid();

    /// <summary>У кабеля НДС назван (20 из 120), у доставки ставка в бумаге не указана.</summary>
    private static readonly Dictionary<Guid, LineVat> Lines = new()
    {
        [Cable] = new(120_000m, 20_000m),
        [Delivery] = new(6_000m, null),
    };

    private static DateOnly D(int month, int day) => new(2026, month, day);

    private static PostedMoney Money(Guid? line, AllocationTarget? target, decimal? amount, DateOnly? on)
    {
        if (target is null) return new(null, amount, on);
        var part = InvoiceAllocation.Create(Guid.NewGuid(), line);
        part.Apply(1, new AllocationValues(target.Value, null, amount));
        return new(part, amount, on);
    }

    /// <summary>Счёт на две стройки и склад: А — в октябре (её сентябрь закрыт), остальное — в сентябре.</summary>
    private static CostInvoice Split(bool unmatched = false, decimal? vatTotal = null) => new(
        Guid.NewGuid(), Supplier, 126_500m, vatTotal, unmatched,
        [
            Money(Cable, AllocationTarget.Site(SiteA), 72_000m, D(10, 1)),
            Money(Cable, AllocationTarget.Site(SiteB), 48_000m, D(9, 15)),
            Money(Delivery, AllocationTarget.Article(Stock), 6_000m, D(9, 15)),
            Money(null, null, 500m, D(9, 15)),
        ]);

    [Fact]
    public void В_период_входят_деньги_его_учётных_месяцев_каждая_часть_своим()
    {
        var invoice = Split();

        var september = SiteCosts.Of([invoice], Lines, D(9, 1), D(9, 30), site: null, withVat: true);
        Assert.Equal(new CostFigure(1, 48_000m), september.Sites[SiteB]);
        Assert.False(september.Sites.ContainsKey(SiteA));
        Assert.Equal(new CostFigure(1, 6_000m), september.Articles[Stock]);
        Assert.Equal(new CostFigure(1, 500m), september.Unallocated);
        // Строки складываются в итог: стройки, вне строек и неразнесённое — и больше ничего.
        Assert.Equal(new CostFigure(1, 54_500m), september.Total);

        var both = SiteCosts.Of([invoice], Lines, D(9, 1), D(10, 31), site: null, withVat: true);
        Assert.Equal(126_500m, both.Total.Amount);
        Assert.Equal(1, both.Total.Invoices);
    }

    [Fact]
    public void На_экране_стройки_только_её_доли_по_контрагентам_без_остатка_и_статей()
    {
        var known = Split();
        var nameless = Split() with { SupplierId = null };

        var site = SiteCosts.Of([known, nameless], Lines, D(9, 1), D(10, 31), SiteA, withVat: true);

        Assert.Empty(site.Sites);
        Assert.Empty(site.Articles);
        Assert.Null(site.Unallocated);
        Assert.Equal(new CostFigure(2, 144_000m), site.Total);
        Assert.Equal([(Supplier, new CostFigure(1, 72_000m)), ((Guid?)null, new CostFigure(1, 72_000m))], site.Suppliers);
    }

    /// <summary>
    /// «Без позиции номенклатуры» — доли таких СЧЕТОВ, а не сумма несопоставленных строк: так велит
    /// формулировка ТЗ («3 счёта на 58 000 ₽») и только так число сходится с реестром.
    /// </summary>
    [Fact]
    public void Несопоставленное_названо_числом_счетов_и_их_деньгами_в_затратах()
    {
        var result = SiteCosts.Of([Split(unmatched: true), Split()], Lines, D(9, 1), D(9, 30), SiteB, withVat: true);

        Assert.Equal(new CostFigure(2, 96_000m), result.Total);
        Assert.Equal(new CostFigure(1, 48_000m), result.Unmatched);
        Assert.Null(SiteCosts.Of([Split()], Lines, D(9, 1), D(9, 30), SiteB, withVat: true).Unmatched);
    }

    /// <summary>
    /// «Без НДС» вычитает НДС там, где он назван, — у строки, а нет у строки — долей «в том числе НДС»
    /// из шапки. Где не назван нигде, часть учтена ПОЛНОЙ суммой и это сказано числом: «ставка не
    /// указана» — не «без НДС».
    /// </summary>
    [Fact]
    public void Без_НДС_вычитает_названный_а_неназванный_называет_числом()
    {
        var bare = SiteCosts.Of([Split()], Lines, D(9, 1), D(10, 31), site: null, withVat: false);
        // Кабель: 72 000 и 48 000 без шестой части; доставка и остаток — как есть.
        Assert.Equal(60_000m, bare.Sites[SiteA].Amount);
        Assert.Equal(40_000m, bare.Sites[SiteB].Amount);
        Assert.Equal(6_000m, bare.Articles[Stock].Amount);
        Assert.Equal(new CostFigure(1, 6_500m), bare.VatUnknown);

        // В шапке НДС назван — доставка и остаток берут его долю, неназванного не остаётся.
        var headed = SiteCosts.Of([Split(vatTotal: 12_650m)], Lines, D(9, 1), D(10, 31), site: null, withVat: false);
        Assert.Equal(5_400m, headed.Articles[Stock].Amount);
        Assert.Equal(450m, headed.Unallocated!.Amount);
        Assert.Null(headed.VatUnknown);

        // С НДС о неназванном не говорят вовсе: вычитать не просили.
        Assert.Null(SiteCosts.Of([Split()], Lines, D(9, 1), D(10, 31), site: null, withVat: true).VatUnknown);
    }

    /// <summary>
    /// «К оплате» от периода не зависит: по всем стройкам — суммы счетов, по стройке — её доли. Счёт,
    /// у которого на стройку нет денег, в её «к оплате» не входит.
    /// </summary>
    [Fact]
    public void К_оплате_суммы_счетов_а_у_стройки_её_доли()
    {
        var unpaid = Split() with { Money = [.. Split().Money.Select(m => m with { AccountingOn = null })] };

        Assert.Equal(new CostFigure(1, 126_500m), SiteCosts.Payable([unpaid], Lines, site: null, withVat: true));
        Assert.Equal(new CostFigure(1, 72_000m), SiteCosts.Payable([unpaid], Lines, SiteA, withVat: true));
        Assert.Equal(new CostFigure(1, 60_000m), SiteCosts.Payable([unpaid], Lines, SiteA, withVat: false));
        Assert.Equal(new CostFigure(0, 0m), SiteCosts.Payable([unpaid], Lines, Guid.NewGuid(), withVat: true));
    }
}
