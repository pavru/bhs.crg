using System.Drawing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>Фрагмент текста с положением в координатах PdfPig (Y вверх, начало внизу-слева).</summary>
public readonly record struct TextFragment(string Text, double Left, double Bottom, double Right, double Top);

/// <summary>
/// Чистый фильтр: оставляет фрагменты, попавшие в регион штампа, и упорядочивает их (сверху вниз,
/// слева направо). Регион задан в РАСТРОВОЙ конвенции (Y вниз от верха, как
/// <see cref="GostTitleBlockRegion"/>), фрагменты — в конвенции PdfPig (Y вверх от низа), поэтому
/// регион переворачивается по Y — через единую <see cref="RasterPdfConvention"/> (там же — оговорка
/// про повороты 0/270 vs непроверенные 90/180).
/// </summary>
public static class StampRegionTextFilter
{
    public static IReadOnlyList<string> InRegion(
        IReadOnlyList<TextFragment> fragments, RectangleF region, double pageHeight)
    {
        double left = region.X, right = region.Right;
        var (pdfBottom, pdfTop) = RasterPdfConvention.ToPdfPigVerticalBounds(region, pageHeight);

        return fragments
            .Where(f =>
            {
                var cx = (f.Left + f.Right) / 2;
                var cy = (f.Bottom + f.Top) / 2;
                return cx >= left && cx <= right && cy >= pdfBottom && cy <= pdfTop;
            })
            .OrderByDescending(f => (f.Bottom + f.Top) / 2) // больше Y в PdfPig = выше на листе
            .ThenBy(f => f.Left)
            .Select(f => f.Text.Trim())
            .Where(t => t.Length > 0)
            .ToList();
    }
}

/// <summary>
/// Извлекает ТОЧНЫЙ текст области штампа страницы PDF из двух источников: текстовый слой
/// (<c>Page.GetWords()</c>) И текст аннотаций (<c>Page.GetAnnotations().Content</c> — часть CAD-
/// экспортов кладёт текст штампа/таблиц не в контент-поток, а в аннотации типа Square). Оба
/// фильтруются по региону штампа (<see cref="GostTitleBlockRegion"/>). Детерминированное чтение
/// байтов PDF — намеренно ОТДЕЛЬНО от вероятностного распознавания (IDocumentRecognizer): результат
/// используется как «опора» (grounding) для vision-LLM либо как прямой источник полей.
/// </summary>
public static class GostStampTextExtractor
{
    /// <summary>Возвращает упорядоченные строки текста из области штампа страницы, либо пустой список,
    /// если текста в регионе нет (чисто графический штамп — ни слоя, ни аннотаций).</summary>
    public static IReadOnlyList<string> Extract(Page page, RectangleF region)
    {
        var fragments = new List<TextFragment>();
        foreach (var word in page.GetWords())
        {
            var bb = word.BoundingBox;
            fragments.Add(new TextFragment(word.Text, bb.Left, bb.Bottom, bb.Right, bb.Top));
        }
        foreach (var a in page.GetAnnotations())
        {
            if (string.IsNullOrWhiteSpace(a.Content)) continue;
            var r = a.Rectangle;
            fragments.Add(new TextFragment(a.Content, r.Left, r.Bottom, r.Right, r.Top));
        }

        return StampRegionTextFilter.InRegion(fragments, region, page.Height);
    }

    /// <summary>Удобная обёртка: открыть PDF из байтов и извлечь для одной страницы (0-based).</summary>
    public static IReadOnlyList<string> Extract(byte[] pdf, int pageIndex, RectangleF region)
    {
        using var doc = PdfDocument.Open(pdf);
        if (pageIndex < 0 || pageIndex >= doc.NumberOfPages) return [];
        return Extract(doc.GetPage(pageIndex + 1), region);
    }
}
