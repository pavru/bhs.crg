using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Distribution;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Арифметика распределения строки (change <c>plan-10-distribution</c>, задача 1.2, ТЗ PLAN-5.1, COST-13): баланс
/// точно в единице запроса, перевод в единицу строки с шестью знаками, остаток — «стройка в целом», состояния строк
/// и счётчики.
/// </summary>
public class DistributionMathTests
{
    private static readonly Guid K1 = Guid.NewGuid();
    private static readonly Guid K2 = Guid.NewGuid();

    /// <summary>«12 · 100 м = 1 200 м»: коэффициент 100.</summary>
    private static LineFacts Cable(decimal sourceVolume = 12m, decimal? factor = 100m) =>
        new(Guid.NewGuid(), "13", "Кабель ВВГнг 3х2,5", sourceVolume, factor, factor is { } k ? sourceVolume * k : null);

    private static IReadOnlyList<StoredPart> Stored(LineFacts row, PartUnit unit, params (Guid, decimal)[] parts)
    {
        var (stored, error) = DistributionMath.Store(row, unit, [.. parts.Select(p => new PartRequest(p.Item1, p.Item2))]);
        Assert.Null(error);
        return stored!;
    }

    private static BalanceError Refused(LineFacts row, PartUnit unit, params (Guid, decimal)[] parts)
    {
        var (stored, error) = DistributionMath.Store(row, unit, [.. parts.Select(p => new PartRequest(p.Item1, p.Item2))]);
        Assert.Null(stored);
        return error!;
    }

    [Fact]
    public void Тысяча_двести_как_семьсот_и_пятьсот_в_единице_плана()
    {
        var row = Cable();
        var parts = Stored(row, PartUnit.Plan, (K1, 700m), (K2, 500m));

        Assert.Equal([7m, 5m], parts.Select(p => p.Quantity));
        Assert.Equal(LineStatus.Distributed, DistributionMath.Status(row.SourceVolume, parts.Sum(p => p.Quantity), null));
        Assert.Equal(700m, DistributionMath.ToPlan(7m, row.Factor));
    }

    [Fact]
    public void Части_сверх_объёма_отказ_с_суммой_и_объёмом()
    {
        var row = Cable();
        var error = Refused(row, PartUnit.Row, (K1, 7m), (K2, 6m));

        Assert.Equal("over", error.Code);
        Assert.Equal(row.RowId, error.RowId);
        Assert.Equal("13", error.Number);
        Assert.Equal(13m, error.Sum);
        Assert.Equal(12m, error.Volume);
        Assert.Equal((13m, 1300m), error.Both(error.Sum));
        Assert.Contains("13 в единице строки = 1300 в единице плана", error.Text);
    }

    [Fact]
    public void Остаток_стройка_в_целом_и_строка_частично()
    {
        var row = Cable();
        var parts = Stored(row, PartUnit.Plan, (K1, 700m));
        var summary = DistributionLines.Evaluate([new LineInput(row.RowId, "1", row.SourceVolume)], NoRules,
            Parts((row.RowId, K1, parts[0].Quantity)));

        var line = Assert.Single(summary.Lines);
        Assert.Equal(LineStatus.Partial, line.Status);
        Assert.Equal(5m, line.Rest);
        Assert.True(line.Whole);
        Assert.Equal([K1], line.Targets);
    }

    [Fact]
    public void Раздел_с_правилом_стройка_в_целом_не_считается_нераспределённым()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var summary = DistributionLines.Evaluate(
            [new LineInput(a, "1", 12m), new LineInput(b, "1", null)],
            Rules(("1", DistributionTarget.WholeConstruction)), Parts());

        Assert.All(summary.Lines, l => Assert.Equal(LineStatus.Whole, l.Status));
        Assert.Equal(0, summary.Tally.Undistributed);
        Assert.Equal(2, summary.Tally.Whole);
        Assert.Equal(0, summary.Sections[new LsrKey("1")].Undistributed);
    }

    [Fact]
    public void Семьсот_метров_при_коэффициенте_2_16_хранятся_с_шестью_знаками_и_баланс_сходится()
    {
        var row = new LineFacts(Guid.NewGuid(), "1", "Кабель", 500m, 2.16m, 1080m);
        var parts = Stored(row, PartUnit.Plan, (K1, 700m), (K2, 380m));

        Assert.Equal(324.074074m, parts[0].Quantity);
        Assert.Equal(500m, parts.Sum(p => p.Quantity));
        Assert.All(parts, p => Assert.Equal(p.Quantity, Math.Round(p.Quantity, 6)));
    }

    [Fact]
    public void Шесть_частей_по_метру_при_коэффициенте_6_сумма_ровно_один()
    {
        var row = new LineFacts(Guid.NewGuid(), "1", "Кабель", 1m, 6m, 6m);
        var sections = Enumerable.Range(0, 6).Select(_ => (Guid.NewGuid(), 1m)).ToArray();
        var parts = Stored(row, PartUnit.Plan, sections);

        Assert.Equal(6, parts.Count);
        Assert.Equal(1m, parts.Sum(p => p.Quantity));
        Assert.Equal(LineStatus.Distributed, DistributionMath.Status(1m, parts.Sum(p => p.Quantity), null));
    }

    [Fact]
    public void Неполный_ввод_с_округлением_не_превышает_объёма()
    {
        // Треть и две трети строки с десятью знаками: округлённые до шести, они не дают суммы больше объёма.
        var row = new LineFacts(Guid.NewGuid(), "1", "Кабель", 1m, 1m, 1m);
        var parts = Stored(row, PartUnit.Row, (K1, 0.3333333333m), (K2, 0.6666666666m));

        Assert.True(parts.Sum(p => p.Quantity) <= 1m);
    }

    [Fact]
    public void Части_у_строки_без_объёма_отказ()
    {
        var row = new LineFacts(Guid.NewGuid(), "7", "Пусконаладка", null, null, null);

        Assert.False(row.Splittable);
        Assert.Equal("no-volume", Refused(row, PartUnit.Row, (K1, 1m)).Code);
        // Правилом раздела такая строка распределяется целиком и считается распределённой.
        Assert.Equal(LineStatus.Distributed, DistributionMath.Status(null, null, DistributionTarget.Section(K1)));
        Assert.Equal(LineStatus.Undistributed, DistributionMath.Status(null, null, null));
    }

    [Fact]
    public void Прочие_отказы_строки()
    {
        Assert.Equal("not-positive", Refused(Cable(), PartUnit.Row, (K1, 0m)).Code);
        Assert.Equal("section-twice", Refused(Cable(), PartUnit.Row, (K1, 1m), (K1, 2m)).Code);
        Assert.Equal("not-matched", Refused(Cable(factor: null), PartUnit.Plan, (K1, 100m)).Code);
        Assert.Equal("too-small", Refused(Cable(), PartUnit.Row, (K1, 0.0000001m)).Code);
    }

    /// <summary>Материал и подчинённая строка распределяются так же — у сводки нет различия по виду строки.</summary>
    [Fact]
    public void Материал_и_подчинённая_строка_распределяются()
    {
        var material = new LineFacts(Guid.NewGuid(), "13.1", "Кабель ВВГнг (материал)", 1200m, null, null);
        var parts = Stored(material, PartUnit.Row, (K1, 700m), (K2, 500m));
        Assert.Equal(1200m, parts.Sum(p => p.Quantity));
    }

    [Fact]
    public void Счётчики_в_сумме_равны_числу_строк()
    {
        var rows = new List<LineInput>
        {
            new(Guid.NewGuid(), "1", 12m), new(Guid.NewGuid(), "1", 12m), new(Guid.NewGuid(), "2", 3m),
            new(Guid.NewGuid(), "2", null), new(Guid.NewGuid(), "3", 5m), new(Guid.NewGuid(), null, 1m),
        };
        var summary = DistributionLines.Evaluate(rows,
            Rules(("1", DistributionTarget.Section(K1)), ("2", DistributionTarget.WholeConstruction)),
            Parts((rows[0].RowId, K2, 12m), (rows[4].RowId, K1, 2m)));
        var t = summary.Tally;

        Assert.Equal(rows.Count, t.Rows);
        Assert.Equal(t.Rows, t.Distributed + t.Partial + t.Whole + t.Undistributed);
        Assert.Equal(2, t.Distributed); // часть на весь объём; правило раздела 1
        Assert.Equal(1, t.Partial);
        Assert.Equal(2, t.Whole);
        Assert.Equal(1, t.Undistributed); // строка вне разделов без правила
        Assert.Equal(1, summary.Sections[new LsrKey("1")].Exceptions);
    }

    private static readonly Dictionary<LsrKey, DistributionTarget> NoRules = [];

    private static Dictionary<LsrKey, DistributionTarget> Rules(params (string? Code, DistributionTarget Target)[] rules) =>
        rules.ToDictionary(r => new LsrKey(r.Code), r => r.Target);

    private static Dictionary<Guid, IReadOnlyList<StoredPart>> Parts(params (Guid Row, Guid Section, decimal Quantity)[] parts) =>
        parts.GroupBy(p => p.Row).ToDictionary(g => g.Key,
            g => (IReadOnlyList<StoredPart>)[.. g.Select(p => new StoredPart(p.Section, p.Quantity))]);
}
