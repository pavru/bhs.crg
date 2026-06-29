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

        var images = PdfRasterizer.ToPngPages(pdf, dpi: 150);

        Assert.Equal(2, images.Count);
        byte[] pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        foreach (var img in images)
        {
            Assert.True(img.Length > 1000, "PNG слишком мал — рендер не сработал");
            Assert.Equal(pngSignature, img.Take(8).ToArray());
        }
    }

    [Fact]
    public void ToPngPages_respects_max_pages()
    {
        var pdf = BuildPdf(5);

        var images = PdfRasterizer.ToPngPages(pdf, dpi: 96, maxPages: 3);

        Assert.Equal(3, images.Count);
    }
}
