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
/// Координаты <c>RectangleF</c> — в единицах PDF при 72 DPI. Ширина/высота страницы должны быть
/// УЖЕ С УЧЁТОМ поворота (PdfPig.Page.Width/Height, не сырой MediaBox до /Rotate).
/// ВАЖНО: <c>PDFtoImage.RenderOptions.Bounds</c> использует Y, растущий ВНИЗ от верхнего края
/// страницы (экранная/растровая конвенция), а НЕ математическую конвенцию PDF (Y=0 внизу,
/// растёт вверх) — вопреки более ранней (ошибочной) экспериментальной оценке. Проверено повторным
/// прямым рендером: регион с Y=0 давал пустой (верхний) фрагмент страницы, регион с
/// Y=pageHeight-height — видимый штамп в правом нижнем углу. Поэтому нижний край считается как
/// <c>pageHeight - height</c>, не 0. См. память проекта project_pdf_gost_split_documents.md.
/// </remarks>
public static class GostTitleBlockRegion
{
    private const float StampWidthMm = 185f;
    private const float StampHeightMm = 55f;
    // Запас на нестандартную разметку — не все организации-разработчики выдерживают рамку
    // ГОСТ миллиметр в миллиметр.
    private const float MarginMm = 15f;

    private const float PointsPerMm = 72f / 25.4f;

    /// <summary>Регион правого нижнего угла страницы для растеризации штампа в высоком разрешении.</summary>
    public static RectangleF ComputeBottomRightRegion(float pageWidth, float pageHeight)
    {
        var width = Math.Min((StampWidthMm + MarginMm) * PointsPerMm, pageWidth);
        var height = Math.Min((StampHeightMm + MarginMm) * PointsPerMm, pageHeight);
        return new RectangleF(pageWidth - width, pageHeight - height, width, height);
    }
}
