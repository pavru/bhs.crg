using BHS.CRG.Infrastructure.Recognition;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace BHS.CRG.Tests;

public class PdfRasterizerTests
{
    private static byte[] BuildPdf(int pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (int i = 1; i <= pages; i++)
        {
            var page = builder.AddPage(595, 842);
            page.AddText($"Test certificate page {i} No 12345", 14, new PdfPoint(50, 780), font);
        }
        return builder.Build();
    }

    [Fact]
    public void ToPngPages_renders_each_page_as_valid_png()
    {
        var pdf = BuildPdf(2);

        var images = PdfRasterizer.ToPngPages(pdf, dpi: 150, maxPages: 10);

        Assert.Equal(2, images.Count);
        byte[] pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        foreach (var img in images)
        {
            Assert.True(img.Length > 1000, "PNG слишком мал — рендер не сработал");
            Assert.Equal(pngSignature, img.Take(8).ToArray());
        }
    }

    /// <summary>
    /// Документ длиннее предела — отказ с обоими числами, а не первые страницы (issue #1271): раньше
    /// одиннадцать страниц молча возвращались десятью, и недочитанное было неотличимо от полного.
    /// </summary>
    [Fact]
    public void Документ_длиннее_предела_не_возвращается_обрезанным()
    {
        var pdf = BuildPdf(11);

        var ex = Assert.Throws<PdfPageLimitException>(() => PdfRasterizer.ToPngPages(pdf, dpi: 96, maxPages: 10));

        Assert.Equal(11, ex.Pages);
        Assert.Equal(10, ex.Limit);
    }

    [Fact]
    public void Документ_ровно_в_предел_возвращается_целиком()
    {
        var pdf = BuildPdf(3);

        Assert.Equal(3, PdfRasterizer.ToPngPages(pdf, dpi: 96, maxPages: 3).Count);
        Assert.Equal(3, PdfRasterizer.PageCount(pdf));
    }

    [Theory]
    [InlineData(1, "1 лист")]
    [InlineData(4, "4 листа")]
    [InlineData(12, "12 листов")]
    [InlineData(22, "22 листа")]
    [InlineData(101, "101 лист")]
    [InlineData(111, "111 листов")]
    public void Число_листов_согласовано_со_словом(int n, string expected)
        => Assert.Equal(expected, RecognitionShared.Sheets(n));
}
