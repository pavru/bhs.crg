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
    private static CostInvoice Split(bool unmatched = false, decimal? vatTotal = null, bool vatByLines = true) => new(
        Guid.NewGuid(), Supplier, 126_500m, vatTotal, unmatched, vatByLines,
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
    /// Срез по разделам — те же доли, сгруппированные иначе (задача G5b, issue #1198): сумма его строк —
    /// итог стройки. Разделы, которых больше нет, — ОДНОЙ строкой: реестр зовёт их одинаково, и счёт на
    /// два таких раздела — один счёт, а не два.
    /// </summary>
    [Fact]
    public void На_экране_стройки_доли_сложены_по_разделам_а_удалённые_разделы_одной_строкой()
    {
        Guid floor = Guid.NewGuid(), goneA = Guid.NewGuid(), goneB = Guid.NewGuid();
        var invoice = new CostInvoice(Guid.NewGuid(), Supplier, 100_000m, null, false, true,
        [
            Money(Cable, AllocationTarget.Site(SiteA, floor), 40_000m, D(9, 15)),
            Money(Cable, AllocationTarget.Site(SiteA), 25_000m, D(9, 15)),
            Money(Cable, AllocationTarget.Site(SiteA, goneA), 10_000m, D(9, 15)),
            Money(Cable, AllocationTarget.Site(SiteA, goneB), 5_000m, D(9, 15)),
            Money(Cable, AllocationTarget.Site(SiteB, floor), 20_000m, D(9, 15)),
        ]);

        // Называет раздел вызывающий — тем же вызовом, что реестр: у двух удалённых название одно.
        SectionName Named(InvoiceAllocation part) =>
            part.SectionId == floor ? new("А / 4 эт.", "4 эт.", floor)
            : part.SectionId is null ? new("А / без раздела", "без раздела", null)
            : new("раздел удалён", "раздел удалён", null);
        var site = SiteCosts.Of([invoice], Lines, D(9, 1), D(9, 30), SiteA, withVat: true, section: Named);

        Assert.Equal(new CostFigure(1, 80_000m), site.Total);
        Assert.Equal(
            [("4 эт.", (Guid?)floor, new CostFigure(1, 40_000m)), ("без раздела", null, new CostFigure(1, 25_000m)), ("раздел удалён", null, new CostFigure(1, 15_000m))],
            site.Sections.Select(s => (s.Section.Short, s.Section.Id, s.Figure)));
        Assert.Equal(site.Total.Amount, site.Sections.Sum(s => s.Figure.Amount));
        Assert.Equal(site.Total.Amount, site.Suppliers.Sum(s => s.Figure.Amount));

        // По всем стройкам среза по разделам нет: раздел без стройки ничего не значит.
        Assert.Empty(SiteCosts.Of([invoice], Lines, D(9, 1), D(9, 30), site: null, withVat: true, section: Named).Sections);
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
    /// «Без НДС» вычитает НДС там, где он назван, — у строки, а если строки об НДС молчат ВСЕ — долей
    /// «в том числе НДС» из шапки. Где не назван нигде, часть учтена ПОЛНОЙ суммой и это сказано числом:
    /// «ставка не указана» — не «без НДС». НДС шапки при строках с НДС уже лежит в них: взять его долю
    /// ещё и на строку без НДС значило бы вычесть один налог дважды (ревью PR #1200).
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

        // НДС назван и в шапке, но у кабеля он свой: шапочный уже лежит в строке кабеля, и доставка с
        // остатком идут полной суммой — названной числом.
        var mixed = SiteCosts.Of([Split(vatTotal: 20_000m)], Lines, D(9, 1), D(10, 31), site: null, withVat: false);
        Assert.Equal(6_000m, mixed.Articles[Stock].Amount);
        Assert.Equal(new CostFigure(1, 6_500m), mixed.VatUnknown);
        Assert.Equal(106_500m, mixed.Total.Amount);

        // Строки об НДС молчат все, а в шапке он назван — каждая часть берёт его долю, неназванного нет.
        Dictionary<Guid, LineVat> silent = new() { [Cable] = new(120_000m, null), [Delivery] = new(6_000m, null) };
        var headed = SiteCosts.Of([Split(vatTotal: 12_650m, vatByLines: false)], silent, D(9, 1), D(10, 31), site: null, withVat: false);
        Assert.Equal(64_800m, headed.Sites[SiteA].Amount);
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

        Assert.Equal((new CostFigure(1, 126_500m), null), SiteCosts.Payable([unpaid], Lines, site: null, withVat: true));
        Assert.Equal((new CostFigure(1, 72_000m), null), SiteCosts.Payable([unpaid], Lines, SiteA, withVat: true));
        Assert.Equal((new CostFigure(1, 60_000m), null), SiteCosts.Payable([unpaid], Lines, SiteA, withVat: false));
        Assert.Equal((new CostFigure(0, 0m), null), SiteCosts.Payable([unpaid], Lines, Guid.NewGuid(), withVat: true));

        // «Без НДС» по всем стройкам — тем же правилом, что по одной: доли складываются в общее, а
        // учтённое полной суммой названо (ревью PR #1200). 60 000 + 40 000 + доставка 6 000 + остаток 500.
        var (all, blind) = SiteCosts.Payable([unpaid], Lines, site: null, withVat: false);
        Assert.Equal(new CostFigure(1, 106_500m), all);
        Assert.Equal(new CostFigure(1, 6_500m), blind);
    }

    /// <summary>
    /// Объект, которого больше нет, реестр называет одним названием на всех — и отчёт складывает такие
    /// деньги в одну строку: по ссылке с этим названием реестр покажет ровно их (ревью PR #1200).
    /// </summary>
    [Fact]
    public void Деньги_на_удалённых_объектах_идут_одной_строкой()
    {
        var result = SiteCosts.Of([Split()], Lines, D(9, 1), D(10, 31), site: null, withVat: true,
            known: new HashSet<Guid> { SiteB });

        Assert.Equal([SiteB], result.Sites.Keys);
        Assert.Empty(result.Articles);
        Assert.Equal(new CostFigure(1, 78_000m), result.Lost);
        Assert.Equal(126_500m, result.Sites[SiteB].Amount + result.Lost!.Amount + result.Unallocated!.Amount);

        Assert.Null(SiteCosts.Of([Split()], Lines, D(9, 1), D(10, 31), site: null, withVat: true).Lost);
    }
}
