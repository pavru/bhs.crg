using System.Drawing;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>
/// ЕДИНАЯ точка, кодирующая связь двух систем координат PDF-страницы в распознавании штампа ГОСТ.
/// Раньше эта связь была продублирована независимо в <see cref="GostTitleBlockRegion"/> и
/// <see cref="StampRegionTextFilter"/> (одна истина в двух копиях — хрупко при смене версии
/// PDFtoImage/PdfPig); сведена сюда.
/// <list type="bullet">
/// <item><b>РАСТРОВАЯ</b> (экранная): Y растёт ВНИЗ от верхнего края. В ней —
///   <c>PDFtoImage.RenderOptions.Bounds</c> (растеризация кропа штампа) и регион
///   <see cref="GostTitleBlockRegion"/>.</item>
/// <item><b>PdfPig</b>: Y растёт ВВЕРХ от нижнего края. В ней PdfPig отдаёт координаты
///   Words/Letters/Annotations.</item>
/// </list>
/// Обе — УЖЕ С УЧЁТОМ поворота страницы (/Rotate): и <c>Page.Width/Height</c>, и координаты букв
/// PdfPig пост-поворотные (подтверждено экспериментально на Rotation=0 и Rotation=270).
/// ⚠ Rotation=90 и 180 на реальных файлах НЕ проверены — код их молча допускает, но соответствие
/// не гарантируется до появления такого файла (см. GostStampCoordinateTests, поворот 0/270).
/// Любой перевод между системами — ТОЛЬКО через этот класс.
/// </summary>
public static class RasterPdfConvention
{
    /// <summary>Растровый Y (вниз от верха) ↔ PdfPig Y (вверх от низа). Инволюция: FlipY(FlipY(y))==y.</summary>
    public static double FlipY(double y, double pageHeight) => pageHeight - y;

    /// <summary>Вертикальные границы растрового региона в координатах PdfPig: (низ, верх).
    /// <paramref name="rasterRegion"/> — как отдаёт <see cref="GostTitleBlockRegion"/>
    /// (<c>RectangleF.Y</c> — верхний край в растровой конвенции).</summary>
    public static (double Bottom, double Top) ToPdfPigVerticalBounds(RectangleF rasterRegion, double pageHeight)
        => (FlipY(rasterRegion.Bottom, pageHeight), FlipY(rasterRegion.Y, pageHeight));
}
