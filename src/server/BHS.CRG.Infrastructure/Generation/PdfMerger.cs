using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace BHS.CRG.Infrastructure.Generation;

/// <summary>
/// Склеивает несколько PDF в один — структурное копирование страниц (PdfSharpCore Import), не
/// переверстка: страницы каждого источника переносятся как есть, в порядке передачи. Нумерация страниц
/// при этом НЕ становится сквозной (номера зашиты внутри каждого исходного PDF при его compile) —
/// та же реальность, что у <see cref="TypstFileMaterializer"/> и <see cref="Recognition.PdfPageSplitter"/>.
/// </summary>
public static class PdfMerger
{
    public static byte[] Merge(IEnumerable<byte[]> pdfs)
    {
        using var outputDoc = new PdfDocument();
        foreach (var bytes in pdfs)
        {
            using var input = new MemoryStream(bytes);
            using var sourceDoc = PdfReader.Open(input, PdfDocumentOpenMode.Import);
            for (var i = 0; i < sourceDoc.PageCount; i++)
                outputDoc.AddPage(sourceDoc.Pages[i]);
        }
        using var output = new MemoryStream();
        outputDoc.Save(output, false);
        return output.ToArray();
    }
}
