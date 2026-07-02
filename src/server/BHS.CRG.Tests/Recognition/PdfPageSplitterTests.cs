using BHS.CRG.Infrastructure.Recognition;
using PigPdfDocument = UglyToad.PdfPig.PdfDocument;
using SharpPdfDocument = PdfSharpCore.Pdf.PdfDocument;

namespace BHS.CRG.Tests.Recognition;

public class PdfPageSplitterTests
{
    private static byte[] MakePdf(int pageCount)
    {
        using var doc = new SharpPdfDocument();
        for (var i = 0; i < pageCount; i++) doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    [Fact]
    public void ExtractPages_ProducesPdfWithExactlyRequestedPageCount()
    {
        var source = MakePdf(5);

        var extracted = PdfPageSplitter.ExtractPages(source, [1, 3]);

        using var result = PigPdfDocument.Open(extracted);
        Assert.Equal(2, result.NumberOfPages);
    }

    [Fact]
    public void ExtractPages_SinglePage_Works()
    {
        var source = MakePdf(3);

        var extracted = PdfPageSplitter.ExtractPages(source, [0]);

        using var result = PigPdfDocument.Open(extracted);
        Assert.Equal(1, result.NumberOfPages);
    }

    [Fact]
    public void ExtractPages_ProducesValidPdfSignature()
    {
        var source = MakePdf(2);
        var extracted = PdfPageSplitter.ExtractPages(source, [0, 1]);

        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(extracted, 0, 4));
    }
}
