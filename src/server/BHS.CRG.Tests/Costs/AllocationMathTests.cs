using BHS.CRG.Modules.Costs.Data;

namespace BHS.CRG.Tests.Costs;

/// <summary>
/// Арифметика разноски (задача F1, issue #1085, ТЗ COST-13) — сторож копеек.
///
/// <para>Чем ломается: округлить каждую часть отдельно и не поправить последнюю. Тогда
/// <see cref="Копейки_сходятся_при_любом_делении" /> падает — сумма частей теряет копейку на трёх
/// частях из ста рублей.</para>
/// </summary>
public class AllocationMathTests
{
    private static readonly Guid LineId = Guid.NewGuid();

    private static AllocationLine Line(decimal? quantity, decimal? amount) => new(LineId, 1, quantity, amount);

    private static AllocationPart ByQuantity(int ordinal, decimal quantity) =>
        new(Guid.NewGuid(), LineId, ordinal, quantity, null);

    private static AllocationPart ByAmount(int ordinal, decimal amount) =>
        new(Guid.NewGuid(), LineId, ordinal, null, amount);

    [Fact]
    public void Сто_рублей_на_три_части_копейка_уходит_в_последнюю()
    {
        var balance = AllocationMath.Line(Line(3, 100m), [ByQuantity(1, 1), ByQuantity(2, 1), ByQuantity(3, 1)]);

        Assert.Equal([33.33m, 33.33m, 33.34m], balance.Parts.Select(p => p.Amount!.Value));
        Assert.Equal([0m, 0m, 0.01m], balance.Parts.Select(p => p.Rounding));
        Assert.True(balance.Balanced);
        Assert.Equal(0m, balance.UnallocatedAmount);
    }

    /// <summary>
    /// Главный сторож: сумма частей равна сумме строки при ЛЮБОМ делении, а поправка последней части не
    /// больше копейки на часть (ТЗ COST-13). Случаи — с неудобными количествами и суммами, на которых
    /// раздельное округление теряет копейки.
    /// </summary>
    [Fact]
    public void Копейки_сходятся_при_любом_делении()
    {
        var random = new Random(1085);

        for (var run = 0; run < 5000; run++)
        {
            var count = random.Next(1, 12);
            var shares = Enumerable.Range(0, count).Select(_ => random.Next(1, 100_000) / 1000m).ToList();
            var quantity = shares.Sum();
            var amount = random.Next(1, 100_000_000) / 100m;

            var parts = shares.Select((q, i) => ByQuantity(i + 1, q)).ToList();
            var balance = AllocationMath.Line(Line(quantity, amount), parts);

            var context = $"прогон {run}: сумма {amount}, части {string.Join(" + ", shares)}";
            Assert.True(balance.Balanced, context);
            Assert.True(amount == balance.Parts.Sum(p => p.Amount!.Value), context);
            Assert.True(0m == balance.UnallocatedAmount, context);

            for (var i = 0; i < count; i++)
            {
                var exact = amount * shares[i] / quantity;
                var drift = Math.Abs(balance.Parts[i].Amount!.Value - exact);
                Assert.True(drift <= 0.01m * count, $"{context}: часть {i + 1} ушла на {drift}");
            }
        }
    }

    /// <summary>Пока строка разнесена не вся, части с остатком всё равно дают сумму строки.</summary>
    [Fact]
    public void Частичная_разноска_остаток_виден_и_сходится_до_копейки()
    {
        // Строка на 300 м по 48,50 — две стройки из трёх разнесены.
        var balance = AllocationMath.Line(Line(300, 14_550m), [ByQuantity(1, 100), ByQuantity(2, 150)]);

        Assert.False(balance.Balanced);
        Assert.Equal(50m, balance.UnallocatedQuantity);
        Assert.Equal(2_425m, balance.UnallocatedAmount);
        Assert.Equal(14_550m, balance.Parts.Sum(p => p.Amount!.Value) + balance.UnallocatedAmount);

        var complete = AllocationMath.Line(Line(300, 14_550m),
            [ByQuantity(1, 100), ByQuantity(2, 150), ByQuantity(3, 50)]);
        Assert.True(complete.Balanced);
        Assert.Equal([4_850m, 7_275m, 2_425m], complete.Parts.Select(p => p.Amount!.Value));
    }

    /// <summary>
    /// Сумма части — доля СУММЫ строки, а не «количество × цена». Бумага дала строке 10 шт. по 10 ₽
    /// сумму 95 ₽ (скидка строкой): «количество × цена» разнесло бы 50 + 45 — вся скидка легла бы в
    /// последнюю часть, нарушив «не больше копейки на часть».
    /// </summary>
    [Fact]
    public void Сумма_строки_из_бумаги_делится_долей_а_не_ценой()
    {
        var balance = AllocationMath.Line(Line(10, 95m), [ByQuantity(1, 5), ByQuantity(2, 5)]);

        Assert.Equal([47.50m, 47.50m], balance.Parts.Select(p => p.Amount!.Value));
    }

    [Fact]
    public void Строка_без_количества_разносится_суммой()
    {
        var balance = AllocationMath.Line(Line(null, 1_500m), [ByAmount(1, 1_000m), ByAmount(2, 500m)]);

        Assert.Equal(AllocationMode.Amount, balance.Mode);
        Assert.True(balance.Balanced);
        Assert.Null(balance.UnallocatedQuantity);
        Assert.Equal(0m, balance.UnallocatedAmount);
    }

    /// <summary>
    /// У строки убрали количество после разноски метрами: части не того вида не разносят ничего, и
    /// выдать их за «разнесено» нельзя.
    /// </summary>
    [Fact]
    public void Часть_не_того_вида_не_разносит_ничего()
    {
        var balance = AllocationMath.Line(Line(null, 1_500m), [ByQuantity(1, 10)]);

        Assert.False(balance.Balanced);
        Assert.True(Assert.Single(balance.Parts).Mismatched);
        Assert.Equal(1_500m, balance.UnallocatedAmount);
    }

    /// <summary>Строку уменьшили после разноски — остаток отрицательный, баланс не сходится.</summary>
    [Fact]
    public void Разнесено_больше_чем_есть_баланс_не_сходится()
    {
        var balance = AllocationMath.Line(Line(250, 250m), [ByQuantity(1, 200), ByQuantity(2, 100)]);

        Assert.False(balance.Balanced);
        Assert.Equal(-50m, balance.UnallocatedQuantity);
    }

    [Fact]
    public void Строка_без_цены_разносится_количеством_суммы_не_знаем()
    {
        var balance = AllocationMath.Line(Line(10, null), [ByQuantity(1, 10)]);

        Assert.True(balance.Balanced);
        Assert.Null(Assert.Single(balance.Parts).Amount);
        Assert.Null(balance.UnallocatedAmount);
    }

    /// <summary>
    /// Расхождение суммы строк с суммой к оплате в пределах допуска уходит в последнюю часть счёта —
    /// и только когда разнесено всё (ТЗ COST-13).
    /// </summary>
    [Fact]
    public void Расхождение_в_допуске_уходит_в_последнюю_часть_счёта()
    {
        var first = new AllocationLine(Guid.NewGuid(), 1, 1, 100m);
        var second = new AllocationLine(Guid.NewGuid(), 2, 2, 200m);
        var parts = new[]
        {
            new AllocationPart(Guid.NewGuid(), first.Id, 1, 1, null),
            new AllocationPart(Guid.NewGuid(), second.Id, 1, 1, null),
            new AllocationPart(Guid.NewGuid(), second.Id, 2, 1, null),
        };

        var balance = AllocationMath.Of([first, second], parts, total: 300.40m);

        Assert.True(balance.Allocated);
        Assert.Equal(0.40m, balance.Discrepancy);
        var last = balance.Lines[1].Parts[1];
        Assert.Equal(100.40m, last.Amount);
        Assert.Equal(0.40m, last.Discrepancy);
        Assert.Equal(300.40m, balance.Lines.SelectMany(l => l.Parts).Sum(p => p.Amount!.Value));
    }

    [Fact]
    public void Расхождение_сверх_допуска_не_даёт_разнесён()
    {
        var line = new AllocationLine(Guid.NewGuid(), 1, 1, 100m);
        var balance = AllocationMath.Of([line], [new AllocationPart(Guid.NewGuid(), line.Id, 1, 1, null)], 105m);

        Assert.False(balance.Allocated);
        Assert.False(balance.WithinTolerance);
        Assert.Equal(100m, balance.Lines[0].Parts[0].Amount);
    }

    [Fact]
    public void Разнесено_не_всё_расхождение_в_части_не_уходит()
    {
        var line = new AllocationLine(Guid.NewGuid(), 1, 2, 100m);
        var balance = AllocationMath.Of([line], [new AllocationPart(Guid.NewGuid(), line.Id, 1, 1, null)], 100.50m);

        Assert.False(balance.Allocated);
        Assert.Equal([1], balance.Unbalanced);
        Assert.Equal(0m, balance.Lines[0].Parts[0].Discrepancy);
    }

    [Fact]
    public void Без_суммы_к_оплате_сверять_не_с_чем()
    {
        var line = new AllocationLine(Guid.NewGuid(), 1, 1, 100m);
        var balance = AllocationMath.Of([line], [new AllocationPart(Guid.NewGuid(), line.Id, 1, 1, null)], null);

        Assert.True(balance.Allocated);
        Assert.Null(balance.Discrepancy);
    }

    /// <summary>Счёт без строк (F2): разноска суммой сходится, когда разнесена вся сумма к оплате.</summary>
    [Fact]
    public void Счёт_без_строк_разнесён_когда_разнесена_вся_сумма_к_оплате()
    {
        AllocationPart Document(decimal amount) => new(Guid.NewGuid(), null, 1, null, amount);

        var half = AllocationMath.Of([], [Document(400m)], 1_000m);
        Assert.Equal(600m, half.Document.UnallocatedAmount);
        Assert.False(half.Allocated);
        Assert.Null(half.Discrepancy);

        Assert.True(AllocationMath.Of([], [Document(400m), Document(600m)], 1_000m).Allocated);
    }

    /// <summary>
    /// Строки появились, а разноска суммой осталась — ждёт пересчёта и «разнесён» не даёт, даже если
    /// строки разнесены сами: сложи их — счёт посчитался бы дважды.
    /// </summary>
    [Fact]
    public void Разноска_суммой_при_строках_ждёт_пересчёта()
    {
        var balance = AllocationMath.Of([Line(1, 100m)], [ByQuantity(1, 1), new(Guid.NewGuid(), null, 1, null, 100m)], 100m);

        Assert.True(balance.Document.Pending);
        Assert.True(balance.Lines[0].Balanced);
        Assert.False(balance.Allocated);
    }
}
