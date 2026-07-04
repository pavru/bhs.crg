using PDFtoImage;
using SkiaSharp;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>
/// Растеризация PDF в PNG-страницы (через PDFium + SkiaSharp) для движков, которые
/// принимают только изображения (Ollama). Рендер выполняется в высоком разрешении
/// и сохраняется в PNG без потерь — качество документа не ухудшается.
/// </summary>
public static class PdfRasterizer
{
    /// <summary>DPI рендера. 300 — стандарт качества OCR; PNG lossless сохраняет всю детализацию.</summary>
    public const int DefaultDpi = 300;

    /// <summary>Предел числа страниц, чтобы не перегрузить модель (сертификаты обычно 1–3 стр.).</summary>
    public const int MaxPages = 10;

    /// <summary>DPI миниатюр для ручного редактора разбиения — низкое, страница нужна только
    /// чтобы визуально узнать документ, не для OCR.</summary>
    public const int ThumbnailDpi = 96;

    /// <summary>Конвертирует PDF в список PNG-страниц (по порядку). Операция CPU-bound.</summary>
    public static IReadOnlyList<byte[]> ToPngPages(byte[] pdf, int dpi = DefaultDpi, int maxPages = MaxPages)
    {
        var pages = new List<byte[]>();
        var options = new RenderOptions(Dpi: dpi);
        // ToImages ленив (yield) — Take не рендерит лишние страницы.
        foreach (var bitmap in Conversion.ToImages(pdf, options: options).Take(maxPages))
        {
            using (bitmap)
            using (var data = bitmap.Encode(SKEncodedImageFormat.Png, 100))
                pages.Add(data.ToArray());
        }
        return pages;
    }

    /// <summary>Рендерит ОДНУ страницу PDF в PNG — для миниатюр в редакторе разбиения
    /// (дешевле, чем растрировать все страницы через ToPngPages, если нужна всего одна).</summary>
    public static byte[] ToPngPage(byte[] pdf, int pageIndex, int dpi = ThumbnailDpi)
    {
        var options = new RenderOptions(Dpi: dpi);
        using var bitmap = Conversion.ToImage(pdf, new Index(pageIndex), options: options);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
