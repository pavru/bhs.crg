using BHS.CRG.Modules.Costs.Data;

namespace BHS.CRG.Tests.Costs;

/// <summary>
/// Арифметика правила срока (ТЗ COST-9.1; задача C4, issue #1080): оба варианта настройки и края —
/// нулевая отсрочка и неизвестное.
/// </summary>
public class InvoiceDueRuleMathTests
{
    private static readonly DateOnly Shipped = new(2026, 10, 1);

    [Theory]
    [InlineData(true, 10, "2026-10-10")]
    [InlineData(false, 10, "2026-10-11")]
    [InlineData(true, 1, "2026-10-01")]
    [InlineData(false, 1, "2026-10-02")]
    // Нулевая отсрочка — день отгрузки при любом правиле: «− 1» поставило бы срок накануне отгрузки.
    [InlineData(true, 0, "2026-10-01")]
    [InlineData(false, 0, "2026-10-01")]
    // Через границу месяца и года — календарём, а не числом дня.
    [InlineData(true, 92, "2026-12-31")]
    [InlineData(false, 92, "2027-01-01")]
    public void Срок_по_правилу(bool shippingDayCounts, int deferral, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), new InvoiceDueRule(shippingDayCounts).DueOf(Shipped, deferral));

    /// <summary>Срок за краем календаря — «не из чего», а не исключение (ревью PR #1328).</summary>
    [Fact]
    public void Срок_за_краем_календаря_не_роняет()
    {
        Assert.Null(new InvoiceDueRule(false).DueOf(new DateOnly(9999, 12, 30), 10));
        Assert.Equal(DateOnly.MaxValue, new InvoiceDueRule(false).DueOf(new DateOnly(9999, 12, 30), 1));
    }

    [Theory]
    [InlineData("2026-10-10", true)]
    [InlineData("2026-10-11", true)]
    [InlineData("2026-10-20", false)]
    public void Срок_по_правилу_узнаётся_по_любому_варианту(string due, bool derived) =>
        Assert.Equal(derived, InvoiceDueRule.Derived(DateOnly.Parse(due), Shipped, 10));

    [Fact]
    public void Без_отгрузки_или_отсрочки_срока_нет()
    {
        var rule = new InvoiceDueRule(true);
        Assert.Null(rule.DueOf(null, 10));
        Assert.Null(rule.DueOf(Shipped, null));
    }
}
