using System.Drawing;
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

    /// <summary>DPI миниатюр для ручного редактора разбиения — низкое, страница нужна только
    /// чтобы визуально узнать документ, не для OCR.</summary>
    public const int ThumbnailDpi = 96;

    /// <summary>
    /// Конвертирует PDF в список PNG-страниц (по порядку) — ВСЕ страницы либо отказ. Операция
    /// CPU-bound.
    ///
    /// <para>⚠️ Документ длиннее <paramref name="maxPages" /> — <see cref="PdfPageLimitException" />,
    /// а не первые страницы (issue #1271). Раньше здесь стоял <c>Take(maxPages)</c> с пределом по
    /// умолчанию: двенадцать страниц «распознавались» по десяти, и результат выглядел полным —
    /// недочитанное неотличимо от отсутствующего. Умолчания у предела поэтому тоже нет: сколько
    /// страниц вызывающий готов принять, он обязан сказать сам и сам же ответить человеку отказом.</para>
    /// </summary>
    public static IReadOnlyList<byte[]> ToPngPages(byte[] pdf, int dpi, int maxPages)
    {
        // Считает тот же PDFium, что и рисует: предел сверяется с тем числом страниц, которое он
        // отдал бы. Второе открытие документа — цена отказа ДО рендера, а не после него.
        var total = Conversion.GetPageCount(pdf);
        if (total > maxPages) throw new PdfPageLimitException(total, maxPages);

        var pages = new List<byte[]>();
        var options = new RenderOptions(Dpi: dpi);
        foreach (var bitmap in Conversion.ToImages(pdf, options: options))
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

    /// <summary>
    /// Рендерит ОБРЕЗАННЫЙ регион одной страницы PDF в PNG (DpiRelativeToBounds=true — <paramref
    /// name="dpi"/> считается относительно региона, не всей страницы) — для второго прохода
    /// распознавания штампа в высоком эффективном разрешении, см. GostTitleBlockRegion.
    /// Координаты <paramref name="bounds"/> — визуальные (с учётом поворота страницы), как
    /// PdfPig.Page.Width/Height, не сырой MediaBox.
    /// </summary>
    public static byte[] ToPngRegion(byte[] pdf, int pageIndex, RectangleF bounds, int dpi = DefaultDpi)
    {
        var options = new RenderOptions(Dpi: dpi, Bounds: bounds, DpiRelativeToBounds: true);
        using var bitmap = Conversion.ToImage(pdf, new Index(pageIndex), options: options);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

/// <summary>
/// В документе больше страниц, чем вызывающий готов принять. Свой тип — чтобы отказ не утонул в
/// общем «файл повреждён или защищён», которым вызывающие отвечают на любой сбой растеризации:
/// файл цел, и чинить его человеку незачем.
/// </summary>
public sealed class PdfPageLimitException(int pages, int limit)
    : Exception($"В документе {pages} стр., предел — {limit}.")
{
    public int Pages { get; } = pages;
    public int Limit { get; } = limit;
}
