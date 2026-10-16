using BHS.CRG.Modules.Costs.Data;

namespace BHS.CRG.Tests.Costs;

/// <summary>
/// Два взгляда на деньги (E2, issue #1100, spec v3.3 п. 1.7): из чего складывается «на момент закрытия»,
/// когда полоса говорит «различаются итоги», а когда «различаются дни», и что значит «без НДС» по строкам
/// книги.
/// </summary>
public class CostViewsTests
{
    private static readonly Guid SiteA = Guid.NewGuid();
    private static readonly Guid SiteB = Guid.NewGuid();
    private static readonly Guid Supplier = Guid.NewGuid();
    private static readonly Guid Invoice = Guid.NewGuid();

    private static DateOnly D(int month, int day) => new(2026, month, day);
    private static readonly (DateOnly From, DateOnly Through) Autumn = (D(9, 1), D(10, 31));

    private static CostMoney On(Guid site, DateOnly day, decimal gross, decimal net, bool vatUnknown = false) =>
        new(Invoice, false, new Grain(new BookCell(AllocationTarget.Site(site), Supplier), day, gross, net, vatUnknown));

    private static ViewsDiffer Differ(CostMoney[] corrected, CostMoney[] closing, Guid? site = null) =>
        CostViews.Differ(corrected, closing, Autumn.From, Autumn.Through, site);

    [Fact]
    public void Полоса_различает_итоги_и_дни()
    {
        CostMoney[] document = [On(SiteA, D(9, 15), 120, 100)];

        Assert.Equal(ViewsDiffer.None, Differ(document, document));
        // Исправление после закрытия: в книге прежние деньги днём оплаты и поправка днём исправления —
        // итог тот же, дни другие.
        Assert.Equal(ViewsDiffer.Details,
            Differ([On(SiteA, D(10, 5), 120, 100)], [On(SiteA, D(9, 15), 120, 100), On(SiteA, D(10, 5), 0, 0)]));
        Assert.Equal(ViewsDiffer.Details,
            Differ([On(SiteB, D(9, 15), 120, 100)], document));
        // Различие только без НДС — тоже итог.
        Assert.Equal(ViewsDiffer.Totals, Differ(document, [On(SiteA, D(9, 15), 120, 90)]));
    }

    [Fact]
    public void Зерно_сравнивается_суммой_строк_и_без_признака_НДС()
    {
        // Две строки книги одного зерна — одно зерно: книга «+120, затем −20» в тот же день равна документу 100.
        Assert.Equal(ViewsDiffer.None,
            Differ([On(SiteA, D(9, 15), 100, 80)], [On(SiteA, D(9, 15), 120, 100), On(SiteA, D(9, 15), -20, -20)]));
        // «НДС неизвестен» исторический и в равенствах не участвует (S33).
        Assert.Equal(ViewsDiffer.None,
            Differ([On(SiteA, D(9, 15), 120, 100)], [On(SiteA, D(9, 15), 120, 100, vatUnknown: true)]));
        // Сложившееся в ноль зерно — не зерно: «+120 и −120» в один день — то же, что пусто.
        Assert.Equal(ViewsDiffer.None, Differ([], [On(SiteA, D(9, 15), 120, 100), On(SiteA, D(9, 15), -120, -100)]));
    }

    [Fact]
    public void Полоса_смотрит_только_отрезок_и_охват_отчёта()
    {
        CostMoney[] corrected = [On(SiteA, D(9, 15), 120, 100), On(SiteB, D(11, 2), 50, 50)];

        // Разница у стройки Б и в ноябре: ни отрезку осени по стройке А, ни ей самой осенью до неё дела нет.
        Assert.Equal(ViewsDiffer.None, Differ(corrected, [On(SiteA, D(9, 15), 120, 100)], SiteA));
        Assert.Equal(ViewsDiffer.None, Differ(corrected, [On(SiteA, D(9, 15), 120, 100), On(SiteB, D(11, 3), 50, 50)]));
        Assert.Equal(ViewsDiffer.Totals, Differ(corrected, [On(SiteA, D(9, 15), 120, 100), On(SiteB, D(10, 3), 50, 50)]));
    }

    [Fact]
    public void На_момент_закрытия_счёт_с_книгой_берётся_строками_книги()
    {
        var target = AllocationTarget.Site(SiteA);
        CostInvoice Paid(bool booked)
        {
            var part = InvoiceAllocation.Create(Guid.NewGuid(), null);
            part.Apply(1, new AllocationValues(target, null, 100m));
            return new(Guid.NewGuid(), Supplier, 100m, null, false, true, [new PostedMoney(part, 100m, D(10, 5))], Booked: booked);
        }
        var (plain, booked) = (Paid(booked: false), Paid(booked: true));
        CostMoney[] book = [new(booked.Id, false, new Grain(new BookCell(target, Supplier), D(9, 15), 100, 100, true))];

        var closing = CostViews.Closing([plain, booked], new Dictionary<Guid, LineVat>(), book);

        Assert.Equal([(plain.Id, D(10, 5)), (booked.Id, D(9, 15))], closing.Select(m => (m.Invoice, m.Grain.Day)));
    }

    /// <summary>
    /// «Без НДС» на момент закрытия (ревизия Архитектора, разд. 6, п. 4): учтено полной суммой — строки
    /// книги с признаком «НДС неизвестен», их суммой без НДС. Признак исторический, поэтому строка, в
    /// которой НДС стал известен, правит прежнюю заглушку и бывает отрицательной (S30 (а): −20 у одного
    /// счёта).
    /// </summary>
    [Fact]
    public void Без_НДС_на_момент_закрытия_называет_строки_с_неизвестным_НДС()
    {
        CostMoney[] book = [On(SiteA, D(9, 15), 120, 120, vatUnknown: true), On(SiteA, D(10, 5), 0, -20, vatUnknown: true)];

        var october = SiteCosts.Of(book, D(10, 1), D(10, 31), SiteA, withVat: false);
        Assert.Equal(new CostFigure(1, -20m), october.Total);
        Assert.Equal(new CostFigure(1, -20m), october.VatUnknown);

        var autumn = SiteCosts.Of(book, Autumn.From, Autumn.Through, SiteA, withVat: false);
        Assert.Equal(new CostFigure(1, 100m), autumn.VatUnknown);
        // С НДС «учтено полной суммой» не называется вовсе.
        Assert.Null(SiteCosts.Of(book, Autumn.From, Autumn.Through, SiteA, withVat: true).VatUnknown);
    }

    /// <summary>
    /// «N счетов» — счета, чьи деньги в строке не ноль (решение владельца 16.10.2026). Исправление
    /// перенесло деньги со стройки А на Б: у А в книге «+120 и −120», и «1 счёт, 0,00» читалось бы как
    /// «счёт здесь есть». Итог считает счёт, посчитанный хоть в одной строке, — и при нулевой сумме счёта:
    /// перенос между объектами законен.
    /// </summary>
    [Fact]
    public void Счёт_с_нулём_в_строке_в_ней_не_считается_а_в_итоге_считается_по_строкам()
    {
        var moved = SiteCosts.Of([On(SiteA, D(9, 15), 120, 100), On(SiteA, D(10, 5), -120, -100), On(SiteB, D(10, 5), 120, 100)],
            Autumn.From, Autumn.Through, null, withVat: true);
        Assert.False(moved.Sites.ContainsKey(SiteA));
        Assert.Equal(new CostFigure(1, 120m), moved.Sites[SiteB]);
        Assert.Equal(new CostFigure(1, 120m), moved.Total);

        // Только перенос в октябре: у А −120, у Б +120 — итог ноль, но счёт в обеих строках.
        var october = SiteCosts.Of([On(SiteA, D(9, 15), 120, 100), On(SiteA, D(10, 5), -120, -100), On(SiteB, D(10, 5), 120, 100)],
            D(10, 1), D(10, 31), null, withVat: true);
        Assert.Equal(new CostFigure(1, -120m), october.Sites[SiteA]);
        Assert.Equal(new CostFigure(1, 0m), october.Total);

        // Сложилось в ноль всё — ни строк, ни счёта в итоге.
        var gone = SiteCosts.Of([On(SiteA, D(9, 15), 120, 100), On(SiteA, D(10, 5), -120, -100)], Autumn.From, Autumn.Through, null, withVat: true);
        Assert.Empty(gone.Sites);
        Assert.Equal(new CostFigure(0, 0m), gone.Total);
        // На экране стройки — та же мера по поставщикам.
        Assert.Empty(SiteCosts.Of([On(SiteA, D(9, 15), 120, 100), On(SiteA, D(10, 5), -120, -100)], Autumn.From, Autumn.Through, SiteA, withVat: true).Suppliers);
    }
}
