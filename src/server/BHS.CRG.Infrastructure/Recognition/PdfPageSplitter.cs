using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>
/// Извлекает подмножество страниц исходного PDF в новый PDF-байтовый массив — структурное
/// копирование (PdfSharpCore.Import), не растрирование: страницы переносятся как есть, без
/// потери качества/векторного содержимого (в отличие от PdfRasterizer, который рендерит в PNG
/// для передачи vision-LLM — это отдельная, независимая операция над теми же исходными байтами).
/// </summary>
public static class PdfPageSplitter
{
    public static byte[] ExtractPages(byte[] sourcePdfBytes, IReadOnlyList<int> pageIndices)
    {
        using var input = new MemoryStream(sourcePdfBytes);
        using var sourceDoc = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        using var outputDoc = new PdfDocument();

        foreach (var index in pageIndices)
            outputDoc.AddPage(sourceDoc.Pages[index]);

        using var output = new MemoryStream();
        outputDoc.Save(output, false);
        return output.ToArray();
    }
}
