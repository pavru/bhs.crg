using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.GrandSmeta;
using BHS.CRG.Modules.Plan.Import;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Вид позиции ЛСР — работа или материал (change <c>plan-31-estimate-ui-carriers</c>, D2, ТЗ PLAN-4) — на всех ЛСР
/// образцов. Числа сняты разбором файлов и сверены по составу: материал — позиция со ссылкой на КА или с шифром
/// сборника цен материалов (ФССЦ, ФСБЦ, ТЦ_).
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
        Assert.DoesNotContain(all, p => p.Code is { } code
            && (code.StartsWith("ГЭСН", StringComparison.Ordinal) || code.StartsWith("ФЕР", StringComparison.Ordinal))
            && EstimatePositionKinds.Of(p) == EstimateRowKind.Material);
        // Всё по КА — материал.
        Assert.All(all.Where(p => p.MarketAnalysis is not null && !(p.Code ?? "").StartsWith("ГЭСН", StringComparison.Ordinal)),
            p => Assert.Equal(EstimateRowKind.Material, EstimatePositionKinds.Of(p)));
    }

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
