using BHS.CRG.Infrastructure.Recognition;

namespace BHS.CRG.Tests.Recognition;

public class StampRegionTextFilterTests
{
    // Данные списаны с реального файла 10761976-АР (стр. 3, H=842, штамп в правом нижнем углу).
    // Координаты фрагментов — в конвенции PdfPig (Y вверх, bottom-left); регион — растровый (Y вниз).
    [Fact]
    public void InRegion_KeepsStampFragments_DropsTableAndBody_OrderedTopToBottom()
    {
        var region = GostTitleBlockRegion.ComputeBottomRightRegion(1191, 842); // Форма3 (наибольший)
        var fragments = new List<TextFragment>
        {
            new("10761976-АР", 957, 144, 1064, 166),          // штамп: шифр
            new("Общие данные", 904, 28, 969, 42),             // штамп: наименование (ниже шифра)
            new("Общие данные", 105, 648, 169, 662),            // ячейка таблицы — вне региона по X
            new("Проектная документация", 640, 374, 1154, 711), // тело листа — вне региона по Y
        };

        var result = StampRegionTextFilter.InRegion(fragments, region, pageHeight: 842);

        // Только штамп; сверху вниз (шифр выше наименования).
        Assert.Equal(["10761976-АР", "Общие данные"], result);
    }

    [Fact]
    public void InRegion_WorksForDifferentPageSize()
    {
        // Стр. 5 того же файла: H=1191, W=1684 — штамп в правом нижнем.
        var region = GostTitleBlockRegion.ComputeBottomRightRegion(1684, 1191);
        var fragments = new List<TextFragment>
        {
            new("10761976-АР", 1448, 145, 1556, 166),
            new("План кровли", 1344, 27, 1513, 42),
            new("вне штампа", 100, 1000, 300, 1020), // верх-лево — исключается
        };

        var result = StampRegionTextFilter.InRegion(fragments, region, pageHeight: 1191);

        Assert.Equal(["10761976-АР", "План кровли"], result);
    }

    [Fact]
    public void InRegion_EmptyInput_ReturnsEmpty()
    {
        var region = GostTitleBlockRegion.ComputeBottomRightRegion(1191, 842);
        Assert.Empty(StampRegionTextFilter.InRegion([], region, 842));
    }

    [Fact]
    public void InRegion_TrimsAndDropsBlankFragments()
    {
        var region = GostTitleBlockRegion.ComputeBottomRightRegion(1191, 842);
        var fragments = new List<TextFragment>
        {
            new("  01-ЭМ  ", 957, 144, 1064, 166),
            new("   ", 950, 100, 1000, 120), // пустое после trim — отбрасывается
        };

        var result = StampRegionTextFilter.InRegion(fragments, region, 842);

        Assert.Equal(["01-ЭМ"], result);
    }
}
