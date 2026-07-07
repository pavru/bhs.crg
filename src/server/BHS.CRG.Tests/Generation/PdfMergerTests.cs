using BHS.CRG.Infrastructure.Generation;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace BHS.CRG.Tests.Generation;

/// <summary>Склейка PDF комплекта: страницы всех источников переносятся по порядку.</summary>
public class PdfMergerTests
{
    [Fact]
    public void Merge_SumsPagesInOrder()
    {
        var a = MakePdf(2);
        var b = MakePdf(3);
        var c = MakePdf(1);

        var merged = PdfMerger.Merge([a, b, c]);

        Assert.Equal(6, PageCount(merged)); // 2 + 3 + 1
    }

    [Fact]
    public void Merge_Single_PreservesPages()
    {
        var merged = PdfMerger.Merge([MakePdf(4)]);
        Assert.Equal(4, PageCount(merged));
    }

    private static byte[] MakePdf(int pages)
    {
        using var doc = new PdfDocument();
        for (var i = 0; i < pages; i++) doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    private static int PageCount(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfReader.Open(ms, PdfDocumentOpenMode.InformationOnly);
        return doc.PageCount;
    }
}
