using BHS.CRG.Infrastructure.Recognition;

namespace BHS.CRG.Tests.Recognition;

public class GostTitleBlockRegionTests
{
    private const float PointsPerMm = 72f / 25.4f;

    [Theory]
    // A4 портрет (595x842), A4 альбом (842x595), A3 альбом (1191x842), крупноформатный
    // чертёж А1 альбом (2384x1684) — все в единицах PDF при 72dpi.
    [InlineData(595f, 842f)]
    [InlineData(842f, 595f)]
    [InlineData(1191f, 842f)]
    [InlineData(2384f, 1684f)]
    public void Region_IsAnchoredToBottomRightCorner(float width, float height)
    {
        var region = GostTitleBlockRegion.ComputeBottomRightRegion(width, height);

        // Правый край региона должен совпадать с правым краем страницы.
        Assert.Equal(width, region.Right, precision: 3);
        // Нижний край региона (Y растёт вниз в системе координат Bounds) должен совпадать
        // с нижним краем страницы, т.е. region.Bottom == height страницы.
        Assert.Equal(height, region.Bottom, precision: 3);
    }

    [Fact]
    public void Region_HasExpectedSizeWithMargin_WhenPageIsLargeEnough()
    {
        var region = GostTitleBlockRegion.ComputeBottomRightRegion(2384f, 1684f);

        var expectedWidth = (185f + 15f) * PointsPerMm;
        var expectedHeight = (55f + 15f) * PointsPerMm;
        Assert.Equal(expectedWidth, region.Width, precision: 3);
        Assert.Equal(expectedHeight, region.Height, precision: 3);
    }

    [Fact]
    public void Region_NeverExceedsPageBounds_OnSmallPage()
    {
        // Гипотетический очень маленький лист — регион не должен вылезать за пределы страницы.
        var region = GostTitleBlockRegion.ComputeBottomRightRegion(100f, 80f);

        Assert.True(region.Width <= 100f);
        Assert.True(region.Height <= 80f);
        Assert.Equal(0f, region.X, precision: 3);
        Assert.Equal(0f, region.Y, precision: 3);
        Assert.Equal(80f, region.Bottom, precision: 3);
    }
}
