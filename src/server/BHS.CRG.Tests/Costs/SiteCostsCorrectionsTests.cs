using BHS.CRG.Modules.Costs.Data;

namespace BHS.CRG.Tests.Costs;

/// <summary>
/// Корректировки закрытых периодов отдельным числом у каждой цифры отчёта (PR 6d E2, разбор Дизайнера п. 7
/// «Месяц, где лежит разница»): строки книги, легшие днём исправления, а не своим днём.
/// </summary>
public class SiteCostsCorrectionsTests
{
    private static readonly Guid SiteA = Guid.NewGuid();
    private static readonly Guid SiteB = Guid.NewGuid();
    private static readonly Guid Supplier = Guid.NewGuid();
    private static readonly Guid Paid = Guid.NewGuid();
    private static readonly Guid Corrected = Guid.NewGuid();

    private static readonly DateOnly October5 = new(2026, 10, 5);

    private static CostMoney Money(Guid invoice, Guid site, decimal gross, bool shifted = false, Guid? section = null) =>
        new(invoice, false, new Grain(new BookCell(AllocationTarget.Site(site, section), Supplier), October5, gross, gross, false, shifted));

    private static SiteCostsResult October(CostMoney[] money, Guid? site = null) =>
        SiteCosts.Of(money, new(2026, 10, 1), new(2026, 10, 31), site, withVat: true,
            section: t => new SectionName($"{t.SectionId}", $"{t.SectionId}", t.SectionId));

    /// <summary>
    /// Октябрь: у стройки А свой оплаченный счёт и корректировка сентябрьского −30, у Б — только
    /// корректировка +30 (деньги перенесли с А на Б). Строка А смешанная, строка Б целиком из корректировок,
    /// а под итогом корректировок нет: перенос между стройками сложился в ноль.
    /// </summary>
    [Fact]
    public void Корректировки_у_строки_и_итога()
    {
        var result = October([Money(Paid, SiteA, 100), Money(Corrected, SiteA, -30, shifted: true), Money(Corrected, SiteB, 30, shifted: true)]);

        Assert.Equal(new CostFigure(2, 70m, new CostCorrections(1, -30m, Whole: false)), result.Sites[SiteA]);
        Assert.Equal(new CostFigure(1, 30m, new CostCorrections(1, 30m, Whole: true)), result.Sites[SiteB]);
        // Итог: корректировки счёта сложились в ноль — «ноль не считается» и здесь, и числа под итогом нет.
        Assert.Equal(new CostFigure(2, 100m), result.Total);
    }

    /// <summary>
    /// Свои +30 и корректировка −30 одного счёта в строке (решение владельца 17.10.2026): правило строки не
    /// меняется — деньги счёта в ней ноль, счёт не посчитан, строки нет. А корректировка под итогом есть: она
    /// считается по своим деньгам, а не по тому, посчитан ли счёт строкой.
    /// </summary>
    [Fact]
    public void Корректировка_под_итогом_и_без_строки()
    {
        var result = October([Money(Corrected, SiteA, 30), Money(Corrected, SiteA, -30, shifted: true)]);

        Assert.Empty(result.Sites);
        Assert.Equal(new CostFigure(0, 0m, new CostCorrections(1, -30m, Whole: false)), result.Total);
    }

    /// <summary>Второй срез — тем же правилом: у раздела свои корректировки.</summary>
    [Fact]
    public void Разделы_несут_свои_корректировки()
    {
        var (x, y) = (Guid.NewGuid(), Guid.NewGuid());
        var result = October([Money(Paid, SiteA, 100, section: x), Money(Corrected, SiteA, -30, shifted: true, section: y)], SiteA);

        Assert.Equal([new CostFigure(1, -30m, new CostCorrections(1, -30m, Whole: true)), new CostFigure(1, 100m)],
            result.Sections.OrderBy(s => s.Figure.Amount).Select(s => s.Figure));
        Assert.Equal(new CostFigure(2, 70m, new CostCorrections(1, -30m, Whole: false)), Assert.Single(result.Suppliers).Figure);
    }

    /// <summary>
    /// Число с корректировкой реестру равно, если равны счета и деньги: реестр корректировок не знает, и
    /// сравнение записей целиком отняло бы стрелку у каждого такого числа.
    /// </summary>
    [Fact]
    public void Равенство_реестру_без_корректировок()
    {
        Assert.True(new CostFigure(2, 70m, new CostCorrections(1, -30m, false)).Same(new CostFigure(2, 70m)));
        Assert.False(new CostFigure(2, 70m).Same(new CostFigure(2, 71m)));
        Assert.False(new CostFigure(2, 70m).Same(null));
    }
}
