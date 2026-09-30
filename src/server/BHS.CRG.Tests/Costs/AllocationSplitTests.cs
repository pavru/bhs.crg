using BHS.CRG.Modules.Costs.Data;

namespace BHS.CRG.Tests.Costs;

/// <summary>
/// Раскладка быстрой разноски (задача F2, issue #1086, ТЗ COST-12): доли складываются ровно в целое,
/// округление — до единицы строки, остаток — в последнюю часть, и отрицательных частей не бывает.
/// </summary>
public class AllocationSplitTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid C = Guid.NewGuid();

    private static SplitTarget[] Equal(params Guid[] sites) => [.. sites.Select(s => new SplitTarget(s, null, 1m))];

    [Fact]
    public void Десять_штук_на_три_объекта_целыми_и_остаток_у_последней()
    {
        var parts = AllocationSplit.Plan([new AllocationLine(Guid.NewGuid(), 1, 10m, 100m)], null, Equal(A, B, C));

        Assert.Equal([3m, 3m, 4m], parts.Select(p => p.Quantity!.Value));
        Assert.Equal([false, false, true], parts.Select(p => p.Remainder));
    }

    [Fact]
    public void Триста_метров_на_три_делятся_без_остатка_и_без_пометки()
    {
        var parts = AllocationSplit.Plan([new AllocationLine(Guid.NewGuid(), 1, 300m, 1m)], null, Equal(A, B, C));

        Assert.All(parts, p => Assert.Equal(100m, p.Quantity));
        Assert.DoesNotContain(parts, p => p.Remainder);
    }

    [Fact]
    public void Сумма_делится_до_копейки()
    {
        var parts = AllocationSplit.Plan([new AllocationLine(Guid.NewGuid(), 1, null, 100m)], null, Equal(A, B, C));

        Assert.Equal([33.33m, 33.33m, 33.34m], parts.Select(p => p.Amount!.Value));
        Assert.True(parts[^1].Remainder);
    }

    [Fact]
    public void Точность_берётся_из_количества_строки()
    {
        Assert.Equal(0, AllocationSplit.Digits(300.000m));
        Assert.Equal(1, AllocationSplit.Digits(12.5m));
        Assert.Equal(3, AllocationSplit.Digits(7.125m));

        var parts = AllocationSplit.Plan([new AllocationLine(Guid.NewGuid(), 1, 12.5m, 10m)], null, Equal(A, B));
        Assert.Equal([6.2m, 6.3m], parts.Select(p => p.Quantity!.Value));
    }

    /// <summary>
    /// ⚠️ Округление к ближайшему дало бы здесь 1 + 1 + 1 и последней −1. Не досталось ничего — части нет:
    /// обе штуки уходят последней стройке, и предпросмотр это показывает.
    /// </summary>
    [Fact]
    public void Две_штуки_на_четыре_объекта_без_отрицательных_частей()
    {
        var parts = AllocationSplit.Plan([new AllocationLine(Guid.NewGuid(), 1, 2m, 2m)], null,
            Equal(A, B, C, Guid.NewGuid()));

        var only = Assert.Single(parts);
        Assert.Equal(2m, only.Quantity);
        Assert.True(only.Remainder);
    }

    /// <summary>
    /// Пересчёт разноски суммой — с точностью колонки, а не до целых: иначе 40 м по трети дали бы 13 + 13 + 14
    /// и сдвинули бы деньги объектов на десятки рублей от решения человека.
    /// </summary>
    [Fact]
    public void Пересчёт_держит_деньги_объектов_а_не_целые_единицы()
    {
        var parts = AllocationSplit.Plan([new AllocationLine(Guid.NewGuid(), 1, 40m, 800m)], null,
            [new SplitTarget(A, null, 333.33m), new SplitTarget(B, null, 333.33m), new SplitTarget(C, null, 333.34m)],
            wholeUnits: false);

        Assert.Equal([13.333m, 13.333m, 13.334m], parts.Select(p => p.Quantity!.Value));
    }

    /// <summary>Доля «не разнесено» считается наравне с целями, но частью не становится.</summary>
    [Fact]
    public void Нерешённая_доля_частью_не_становится()
    {
        var parts = AllocationSplit.Plan([new AllocationLine(Guid.NewGuid(), 1, 50m, 1_000m)], null,
            [new SplitTarget(A, null, 300m), new SplitTarget(Guid.Empty, null, 700m, Unallocated: true)], wholeUnits: false);

        var only = Assert.Single(parts);
        Assert.Equal(15m, only.Quantity);
        Assert.False(only.Remainder);
    }

    [Fact]
    public void Счёт_без_строк_делится_суммой_к_оплате()
    {
        var parts = AllocationSplit.Plan([], 1_000m, [new SplitTarget(A, null, 25m), new SplitTarget(B, null, 75m)]);

        Assert.All(parts, p => Assert.Null(p.LineId));
        Assert.Equal([250m, 750m], parts.Select(p => p.Amount!.Value));
    }

    [Fact]
    public void Строка_без_количества_и_суммы_не_раскладывается()
    {
        Assert.Empty(AllocationSplit.Plan([new AllocationLine(Guid.NewGuid(), 1, null, null)], null, Equal(A)));
    }

    /// <summary>
    /// Сторож на случайных числах: сумма долей ровно равна целому, доли не меньше нуля, последняя не меньше
    /// своей округлённой доли. Ломается округлением к ближайшему и «последней без поправки».
    /// </summary>
    [Fact]
    public void Доли_всегда_складываются_в_целое_и_не_отрицательны()
    {
        var random = new Random(1086);

        for (var run = 0; run < 5_000; run++)
        {
            var digits = random.Next(0, 4);
            var whole = decimal.Round((decimal)(random.NextDouble() * 10_000), digits);
            if (whole == 0) continue;

            var weights = Enumerable.Range(0, random.Next(1, 9)).Select(_ => (decimal)random.Next(1, 100)).ToList();
            var shares = AllocationSplit.Split(whole, weights, digits);

            Assert.Equal(whole, shares.Sum());
            Assert.All(shares, s => Assert.True(s >= 0, $"{whole} на {string.Join("/", weights)}: доля {s}"));
            Assert.All(shares, s => Assert.Equal(s, decimal.Round(s, digits)));
        }
    }
}
