using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.GrandSmeta;
using BHS.CRG.Modules.Plan.Import;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Вид позиции ЛСР — работа или материал (change <c>plan-31-estimate-ui-carriers</c>, D2, ТЗ PLAN-4) — на всех ЛСР
/// образцов. Числа сняты разбором файлов и сверены по составу: материал — позиция со ссылкой на КА или с шифром
/// сборника цен материалов (ФССЦ, ФСБЦ, ТЦ_). Независимо от разбора правило сверяется с эталонами печатных форм
/// программы (<see cref="Вид_позиции_как_в_печатной_форме" />).
/// </summary>
public class EstimatePositionKindTests
{
    /// <summary>Строк-материалов в каждой ЛСР: ЭОМ — 139 по КА (ТЦ_), 5 ФССЦ, 1 ФСБЦ; РИМ — «Счёт-фактура» и
    /// ФСБЦ по КА, ФСБЦ без КА.</summary>
    public static TheoryData<string, int, int> Samples => new()
    {
        { "lsr-16.2.0-vn", 25, 14 },
        { "lsr-16.2.2-eom", 238, 145 },
        { "lsr-16.2.2-os-skud", 75, 34 },
        { "lsr-16.2.2-sks", 172, 111 },
        { "lsr-16.2.3-rim1", 43, 26 },
        { "lsr-16.2.3-rim2", 34, 21 },
        { "lsr-16.3.0-vn-no-itog", 25, 14 },
        { "lsr-16.3.0-eom-no-itog", 238, 145 },
        { "lsr-16.3.0-os-skud-no-itog", 75, 34 },
        { "lsr-16.3.0-sks-no-itog", 172, 111 },
        { "lsr-16.3.0-rim1-no-itog", 43, 26 },
        { "lsr-16.3.0-rim2-no-itog", 34, 21 },
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void Строк_материалов_в_ЛСР_как_в_эталоне_и_ни_одной_расценки(string sample, int positions, int materials)
    {
        var model = LocalEstimateReader.Read(GrandSmetaSamples.Open(sample));
        var all = model.Positions.ToList();
        Assert.Equal(positions, all.Count);
        Assert.Equal(materials, all.Count(p => EstimatePositionKinds.Of(p) == EstimateRowKind.Material));

        // Расценка не становится материалом никогда — даже рядом со ссылкой на КА.
        Assert.DoesNotContain(all, p => IsRate(p.Code) && EstimatePositionKinds.Of(p) == EstimateRowKind.Material);
    }

    /// <summary>
    /// Позиции эталонов — контрольные точки, снятые с печатной формы программы, а не с XML (<c>etalons_from_forms.py</c>):
    /// по форме позиция со ссылкой на КА — материал, расценка ГЭСН/ФЕР — работа. Разобранная позиция с тем же
    /// номером обязана получить тот же вид.
    /// </summary>
    [Theory]
    [MemberData(nameof(Etalons))]
    public void Вид_позиции_как_в_печатной_форме(string sample)
    {
        var parsed = LocalEstimateReader.Read(GrandSmetaSamples.Open(sample)).Positions.ToDictionary(p => p.Number);
        var checkedAny = false;
        foreach (var form in GrandSmetaSamples.Etalon(sample).Arr("positions"))
        {
            var expected = form.Has("marketAnalysis") ? EstimateRowKind.Material
                : IsRate(form.Str("code")) ? EstimateRowKind.Work
                : (EstimateRowKind?)null;
            if (expected is null) continue;
            checkedAny = true;
            Assert.Equal(expected, EstimatePositionKinds.Of(parsed[form.Str("number")!]));
        }
        Assert.True(checkedAny, $"В эталоне {sample} нет ни одной позиции с видом по форме.");
    }

    public static TheoryData<string> Etalons => new(GrandSmetaSamples.Lsr);

    /// <summary>Код материала строки-материала — только шифр сборника цен: номер счёт-фактуры у позиций по КА —
    /// один на весь документ, и запомненное по нему подставило бы выбор одной позиции всем.</summary>
    [Fact]
    public void Код_материала_только_шифр_сборника_цен()
    {
        var rim = LocalEstimateReader.Read(GrandSmetaSamples.Open("lsr-16.2.3-rim1")).Positions.ToList();
        var invoice = rim.Where(p => (p.Code ?? "").StartsWith("Счёт-фактура", StringComparison.Ordinal)).ToList();
        Assert.True(invoice.Count > 1);
        Assert.All(invoice, p =>
        {
            Assert.Equal(EstimateRowKind.Material, EstimatePositionKinds.Of(p));
            Assert.Null(EstimatePositionKinds.MaterialCode(p));
        });
        var fsbc = rim.First(p => (p.Code ?? "").StartsWith("ФСБЦ-", StringComparison.Ordinal));
        Assert.Equal(fsbc.Code, EstimatePositionKinds.MaterialCode(fsbc));
    }

    private static bool IsRate(string? code) =>
        code is not null && (code.StartsWith("ГЭСН", StringComparison.Ordinal) || code.StartsWith("ФЕР", StringComparison.Ordinal));

    /// <summary>Спорное — работа (Risks design): вспомогательная позиция, контроль гофры, позиция без шифра.</summary>
    [Fact]
    public void Спорные_позиции_остаются_работой()
    {
        var eom = LocalEstimateReader.Read(GrandSmetaSamples.Open("lsr-16.2.2-eom")).Positions.ToList();
        Assert.Equal(EstimateRowKind.Work, EstimatePositionKinds.Of(Assert.Single(eom, p => p.Code == "Вспомогательная позиция")));
        Assert.Equal(EstimateRowKind.Work, EstimatePositionKinds.Of(Assert.Single(eom, p => p.Number == "23.7")));
        Assert.Null(Assert.Single(eom, p => p.Number == "23.7").Code);

        var osSkud = LocalEstimateReader.Read(GrandSmetaSamples.Open("lsr-16.2.2-os-skud")).Positions.ToList();
        Assert.All(osSkud.Where(p => p.Code == "Контроль гофры"), p => Assert.Equal(EstimateRowKind.Work, EstimatePositionKinds.Of(p)));
        // ТЦ_ без ссылки на КА — материал по шифру сборника.
        var tc = Assert.Single(osSkud, p => p.Number == "76");
        Assert.Null(tc.MarketAnalysis);
        Assert.Equal(EstimateRowKind.Material, EstimatePositionKinds.Of(tc));
    }
}
