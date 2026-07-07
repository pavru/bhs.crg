using System.Drawing;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>
/// Регион основной надписи (штампа) по ГОСТ Р 21.101-2020 — правый нижний угол листа,
/// фиксированный физический размер независимо от формата листа (А4/А3/А2/А1, портрет/альбом).
/// Форма 3 (чертёж, первый лист) — графы шириной 185мм, высотой ~55мм; форма 6 (последующие
/// листы) — те же 185мм, высотой ~15мм (подмножество формы 3 по высоте). Берём больший (форма 3)
/// размер с запасом — гарантированно накрывает и форму 6, не нужно заранее знать, какая это форма.
/// </summary>
/// <remarks>
/// Координаты <c>RectangleF</c> — в единицах PDF при 72 DPI, в РАСТРОВОЙ конвенции (Y вниз от верха —
/// как ждёт <c>PDFtoImage.RenderOptions.Bounds</c>). Каноническое описание связи с координатами
/// PdfPig и оговорка про повороты — в <see cref="RasterPdfConvention"/> (единая точка).
/// Ширина/высота страницы должны быть УЖЕ С УЧЁТОМ поворота (PdfPig.Page.Width/Height, не сырой
/// MediaBox до /Rotate). Нижний край региона — <c>pageHeight - height</c>, не 0 (проверено прямым
/// рендером: Y=0 давал пустой верхний фрагмент, Y=pageHeight-height — штамп в правом нижнем углу;
/// см. память проекта project_pdf_gost_split_documents.md).
/// </remarks>
public static class GostTitleBlockRegion
{
    private const float StampWidthMm = 185f; // ширина одна и та же для всех форм 3-6
    private const float Form3Or4HeightMm = 55f; // чертежи/схемы, строительные изделия — первый лист
    private const float Form5HeightMm = 40f; // текстовые документы — первый/заглавный лист
    private const float Form6HeightMm = 15f; // ЛЮБОЙ последующий лист — табличка заметно меньше
    // Запас на нестандартную разметку — не все организации-разработчики выдерживают рамку
    // ГОСТ миллиметр в миллиметр.
    private const float MarginMm = 15f;

    private const float PointsPerMm = 72f / 25.4f;

    /// <summary>
    /// Регион правого нижнего угла страницы для растеризации штампа в высоком разрешении.
    /// Высота региона зависит от распознанной формы (<paramref name="form"/> — значение поля
    /// <see cref="GostTitleBlockFields.StampFormPath"/> из первого прохода распознавания):
    /// Форма3/Форма4 — 55мм, Форма5 — 40мм, Форма6 — 15мм. Форма не распознана/неизвестна —
    /// берём больший (Форма3/4) размер как безопасный по умолчанию (не промахнётся мимо штампа).
    /// </summary>
    public static RectangleF ComputeBottomRightRegion(float pageWidth, float pageHeight, string? form = null)
    {
        var heightMm = form switch
        {
            "Форма5" => Form5HeightMm,
            "Форма6" => Form6HeightMm,
            _ => Form3Or4HeightMm,
        };
        var width = Math.Min((StampWidthMm + MarginMm) * PointsPerMm, pageWidth);
        var height = Math.Min((heightMm + MarginMm) * PointsPerMm, pageHeight);
        return new RectangleF(pageWidth - width, pageHeight - height, width, height);
    }
}
