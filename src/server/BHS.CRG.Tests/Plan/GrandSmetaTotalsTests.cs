using System.Xml.Linq;
using BHS.CRG.Modules.Plan.GrandSmeta;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Досчёт итога ЛСР, сохранённой без итогов (change <c>plan-05-grand-smeta-reader</c>, группа 3, ТЗ PLAN-8): на
/// парах владельца «с итогом / без итогов» (4 БИМ, 2 РИМ) досчитанный итог каждой позиции равен итогу программы
/// до копейки; метод или опция вне закрытого перечня — отказ с названием, а не догадка.
/// </summary>
public class GrandSmetaTotalsTests
{
    public static TheoryData<string, string> Pairs
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var (noTotals, withTotals) in GrandSmetaSamples.Pairs) data.Add(noTotals, withTotals);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Досчитанный_итог_каждой_позиции_равен_итогу_программы(string noTotals, string withTotals)
    {
        var computed = LocalEstimateReader.Read(GrandSmetaSamples.Open(noTotals));
        var program = LocalEstimateReader.Read(GrandSmetaSamples.Open(withTotals));

        Assert.Empty(computed.Unparsed);
        Assert.Equal(program.Method, computed.Method);
        Assert.Equal(program.PositionsInFile, computed.PositionsInFile);
        var expected = program.Positions.ToDictionary(Key);
        foreach (var p in computed.Positions)
        {
            var want = expected[Key(p)];
            Assert.Equal((Key(p), want.Total), (Key(p), p.Total));
            Assert.Equal((Key(p), want.Labor ?? 0m), (Key(p), p.Labor));
            // Тот же набор; порядок у программы свой и из файла не выводится
            Assert.Equal(want.Coefficients.Select(c => c.Code).Order(), p.Coefficients.Select(c => c.Code).Order());
            Assert.Equal(want.Materials.Select(m => m.Quantity), p.Materials.Select(m => m.Quantity));
            Assert.Equal(want.UnitPrice, p.UnitPrice);
        }

        Assert.Equal(program.Chapters.Select(c => c.Total), computed.Chapters.Select(c => c.Total));
        // Нулевые затраты труда раздела (одни строки ресурсов) программа не пишет вовсе
        Assert.Equal(program.Chapters.Select(c => (decimal?)(c.Labor ?? 0m)), computed.Chapters.Select(c => c.Labor));
        Assert.Equal(program.Totals.Total, computed.Totals.Total);
        Assert.Equal(program.Totals.Labor ?? 0m, computed.Totals.Labor);
    }

    /// <summary>Итог, записанный программой, не пересчитывается (PLAN-8): источник — «программа»; досчитанный —
    /// «посчитано системой», и «ВСЕГО по смете» с лимитированными затратами у него нет: их система не досчитывает.</summary>
    [Theory]
    [MemberData(nameof(Pairs))]
    public void Источник_итога_программа_или_посчитано_системой(string noTotals, string withTotals)
    {
        var computed = LocalEstimateReader.Read(GrandSmetaSamples.Open(noTotals));
        var program = LocalEstimateReader.Read(GrandSmetaSamples.Open(withTotals));

        Assert.All(computed.Positions, p => Assert.Equal(TotalSource.ComputedBySystem, p.TotalSource));
        Assert.All(computed.Positions, p => Assert.Null(p.TotalBase));
        Assert.Null(computed.Totals.EstimateTotal);
        Assert.All(program.Positions, p => Assert.Equal(TotalSource.Program, p.TotalSource));
        Assert.NotNull(program.Totals.EstimateTotal);
    }

    [Fact]
    public void Смета_РМ_без_итогов_отвергнута_с_названием_метода()
    {
        // Без индексов к позиции и к ресурсам — РМ: у БИМ-образца есть и IndexesRes, без них он читался бы РИМ
        var file = GrandSmetaSamples.OpenEditedXml("lsr-16.3.0-vn-no-itog", x =>
        {
            var indexes = x.Root!.Element("Indexes")!;
            indexes.SetAttributeValue("IndexesMode", "None");
            indexes.Elements("IndexesRes").Remove();
        });

        var ex = Assert.Throws<GrandSmetaRefusedException>(() => LocalEstimateReader.Read(file));

        Assert.Equal(GrandSmetaRefusal.NoTotals, ex.Reason);
        Assert.StartsWith("Смета без итогов, метод РМ: досчёт не поддерживается — обратитесь к разработчику.", ex.Message);
    }

    [Theory]
    [InlineData("lsr-16.3.0-vn-no-itog", "Options=\"AddZatr ", "Options=\"AddZatr RoundPos ", "метод БИМ (опция RoundPos)")]
    [InlineData("lsr-16.3.0-rim1-no-itog", "Options=\"AddZatr ZpmByTz ", "Options=\"AddZatr ", "метод РИМ (нет опции ZpmByTz)")]
    [InlineData("lsr-16.3.0-vn-no-itog", "IndexesLinkMode=\"Category\"", "IndexesLinkMode=\"Default\"", "метод БИМ (индексы PosOne, привязка Default)")]
    public void Настройка_вне_перечня_отвергнута_с_её_названием(string sample, string from, string to, string named)
    {
        var file = GrandSmetaSamples.OpenEdited(sample, x => x.Replace(from, to));

        var ex = Assert.Throws<GrandSmetaRefusedException>(() => LocalEstimateReader.Read(file));

        Assert.Equal(GrandSmetaRefusal.NoTotals, ex.Reason);
        Assert.Contains($"Смета без итогов, {named}: досчёт не поддерживается", ex.Message);
    }

    /// <summary>Индекс, названный у позиции, но не найденный в документе, — позиция не досчитана и названа, а не
    /// посчитана без индекса; итога её раздела нет — сумма без неё не итог.</summary>
    [Fact]
    public void Позиция_без_своего_индекса_уходит_в_не_разобранные()
    {
        var file = GrandSmetaSamples.OpenEdited("lsr-16.3.0-vn-no-itog",
            x => x.Replace("IndexCode=\"(25), ФЕР, 2 кв 2026 (СМР)\"", "IndexCode=\"нет такого\""));

        var m = LocalEstimateReader.Read(file);

        Assert.NotEmpty(m.Unparsed);
        Assert.All(m.Unparsed, u => Assert.Contains("индекс «нет такого» не найден", u.Reason));
        Assert.Contains(m.Chapters, c => c.Total is null);
        Assert.Null(m.Totals.Total);
    }

    /// <summary>Номер позиции сквозной по документу («13», подчинённая — «1.1») — ключ пары.</summary>
    private static string Key(LocalEstimatePosition p) => p.Number;
}
