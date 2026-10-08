using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// NPOI и PdfSharpCore работают с той версией ImageSharp, которая прибита в проекте.
///
/// <para>Обе библиотеки собраны под ImageSharp 3.x, а в проекте стоит 4.x: у 3.1.12 вышли
/// уведомления об уязвимостях, исправление есть только в следующей мажорной версии. Несовместимость
/// мажорных версий проявляется не при сборке, а во время работы — <c>MissingMethodException</c> в ту
/// минуту, когда библиотека сама позовёт ImageSharp: картинка в книге, изображение на странице PDF,
/// автоширина столбца.</para>
///
/// <para>⚠️ Наш код этих путей сегодня не зовёт (PDF только режется и склеивается, книги пишутся без
/// картинок) — поэтому остальной набор тестов совместимость НЕ подтверждает. Этот сторож проходит их
/// нарочно: первый, кто добавит логотип в выгрузку, не должен узнать о несовместимости от заказчика.
/// Упал после смены версии любого из трёх пакетов — версии разошлись; чинится подбором версий, а не
/// правкой теста.</para>
/// </summary>
public class ImageSharpCompatibilityTests
{
    /// <summary>PNG 1×1 — наименьший настоящий файл: декодер проходит заголовок, палитру и данные.</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    /// <summary>
    /// ⚠️ Закреплён ОТКАЗ, а не работа: PdfSharpCore 1.3.67 собран под ImageSharp 1.x и зовёт метод,
    /// которого нет уже в 3.x — изображение на страницу PDF он не кладёт ни на прежней версии, ни на
    /// этой. Нашему коду это не мешает: PDF он только режет и склеивает. Тест стоит, чтобы тот, кому
    /// понадобится картинка в PDF, узнал об этом здесь; а начни он падать (отказа больше нет) —
    /// библиотеку обновили, и предупреждение в DEV_NOTES («Поставка») пора снять.
    /// </summary>
    [Fact]
    public void PdfSharpCore_изображение_не_читает_и_это_известно()
    {
        using var document = new PdfDocument();
        document.AddPage();

        Assert.Throws<MissingMethodException>(() => XImage.FromStream(() => new MemoryStream(Png)));

        // Без изображений — работает: так им и пользуется приложение.
        using var saved = new MemoryStream();
        document.Save(saved);
        Assert.True(saved.Length > 0);
    }

    [Fact]
    public void NPOI_пишет_книгу_с_картинкой_и_автошириной()
    {
        using var book = new XSSFWorkbook();
        var sheet = book.CreateSheet("Лист");
        sheet.CreateRow(0).CreateCell(0).SetCellValue("Наименование позиции с длинным текстом");

        var picture = book.AddPicture(Png, PictureType.PNG);
        var anchor = book.GetCreationHelper().CreateClientAnchor();
        anchor.Col1 = 1;
        anchor.Row1 = 1;
        var placed = sheet.CreateDrawingPatriarch().CreatePicture(anchor, picture);
        // Размер картинки NPOI узнаёт, декодируя её, — это и есть обращение к ImageSharp.
        placed.Resize();
        // Автоширина меряет текст шрифтом — через SixLabors.Fonts, соседа ImageSharp по версии.
        sheet.AutoSizeColumn(0);

        using var saved = new MemoryStream();
        book.Write(saved, leaveOpen: true);
        Assert.True(saved.Length > 0);
    }
}
