using System.Drawing;
using BHS.CRG.Infrastructure.Recognition;

namespace BHS.CRG.Tests.Recognition;

/// <summary>
/// Согласование двух систем координат распознавания штампа: растровый регион
/// (<see cref="GostTitleBlockRegion"/>) и переворот в PdfPig (<see cref="StampRegionTextFilter"/>
/// через единую <see cref="RasterPdfConvention"/>). Повороты 0 (портрет) и 270 (альбом) — как
/// пост-поворотные размеры страницы; 90/180 намеренно НЕ покрыты (нет реального файла — см.
/// RasterPdfConvention). Чистые числа, без PDF/IO.
/// </summary>
public class GostStampCoordinateTests
{
    // A4 в пунктах (72 DPI), пост-поворотные размеры.
    private const float A4Short = 595.276f;
    private const float A4Long = 841.89f;

    /// <summary>Фрагмент-точка с центром в (cx, cy) в координатах PdfPig.</summary>
    private static TextFragment At(string text, double cx, double cy) =>
        new(text, cx - 1, cy - 1, cx + 1, cy + 1);

    [Fact]
    public void FlipY_IsInvolution()
    {
        const double h = A4Long;
        Assert.Equal(100, RasterPdfConvention.FlipY(h - 100, h), 3);
        Assert.Equal(42, RasterPdfConvention.FlipY(RasterPdfConvention.FlipY(42, h), h), 6);
    }

    [Fact]
    public void ToPdfPigVerticalBounds_BottomRasterRegion_MapsToBottomStripInPdfPig()
    {
        // Регион у нижнего края в растре (Y = h - height … h) → в PdfPig это полоса [0 … height].
        var region = GostTitleBlockRegion.ComputeBottomRightRegion(A4Short, A4Long);
        var (bottom, top) = RasterPdfConvention.ToPdfPigVerticalBounds(region, A4Long);
        Assert.Equal(0, bottom, 3);
        Assert.Equal(region.Height, top, 3);
    }

    [Fact]
    public void ComputeBottomRightRegion_PlacesRegionAtVisualBottom_NotTop()
    {
        // Регресс-гард прежнего Y-бага: нижний край региона у самого низа листа (Bottom≈pageHeight),
        // верхний край НЕ у 0.
        var region = GostTitleBlockRegion.ComputeBottomRightRegion(A4Short, A4Long);
        Assert.Equal(A4Long, region.Bottom, 3);
        Assert.True(region.Y > A4Long / 2, "верх региона штампа должен быть в нижней половине листа");
    }

    [Theory]
    [InlineData(A4Short, A4Long)]  // портрет (Rotation 0)
    [InlineData(A4Long, A4Short)]  // альбом  (Rotation 270)
    public void StampCornerLetter_IsInRegion_TopLeftLetter_IsNot(float pageWidth, float pageHeight)
    {
        var region = GostTitleBlockRegion.ComputeBottomRightRegion(pageWidth, pageHeight);
        var fragments = new[]
        {
            At("ШТАМП", pageWidth - 10, 10),   // правый нижний угол (PdfPig: большой X, малый Y)
            At("ШАПКА", 10, pageHeight - 10),  // левый верхний угол — вне штампа
        };

        var inside = StampRegionTextFilter.InRegion(fragments, region, pageHeight);

        Assert.Contains("ШТАМП", inside);
        Assert.DoesNotContain("ШАПКА", inside);
    }

    [Fact]
    public void InRegion_OrdersFragments_TopToBottom_LeftToRight()
    {
        var region = GostTitleBlockRegion.ComputeBottomRightRegion(A4Short, A4Long);
        // Внутри штампа: верхняя строка (больший Y в PdfPig) раньше нижней; в строке — левее раньше.
        var fragments = new[]
        {
            At("низ",   A4Short - 20, 20),
            At("верх-Л", A4Short - 40, 120),
            At("верх-П", A4Short - 15, 120),
        };

        var ordered = StampRegionTextFilter.InRegion(fragments, region, A4Long);

        Assert.Equal(["верх-Л", "верх-П", "низ"], ordered);
    }
}
