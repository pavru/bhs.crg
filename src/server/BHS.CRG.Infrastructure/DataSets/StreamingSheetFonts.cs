using System.Reflection;
using NPOI.XSSF.Streaming;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Потоковый лист NPOI — без замера шрифтом машины (issue #1754). NPOI 2.7 заводит трекер автоширины
/// при сбросе ПЕРВОЙ же строки, хотя автоширину никто не просил, а трекер в конструкторе меряет знак
/// «0» шрифтом книги — системным, через SixLabors.Fonts. Calibri и Arial на Linux обычно нет, и NPOI
/// берёт первое попавшееся семейство машины. Окажись им цветной Noto Color Emoji (растровый, без
/// таблицы <c>loca</c>) — падает КАЖДАЯ выгрузка xlsx: <c>MissingFontTableException</c>. Какое
/// семейство первое, решает порядок файлов на диске: на одних раннерах CI проходило, на других
/// падало, а на сервере заказчика зависело бы от того, какие шрифты там стоят.
///
/// <para>Трекер NPOI держит в <c>Lazy</c> и читает через <c>.Value?.</c>, поэтому <c>Lazy</c>,
/// отдающий <c>null</c>, выключает его целиком. Ширину колонок <see cref="SpreadsheetExporter" />
/// считает сам. Плата — <c>AutoSizeColumn</c> у такого листа не работает вовсе.</para>
///
/// <para>⚠️ Поле закрытое, и обход постоянный: переход на ветку 2.8, где трекер заводится по
/// требованию, закрыт решением владельца (issue #1756, DEV_NOTES «Поставка»), и обход живёт, пока
/// живёт NPOI 2.7. Сменись устройство поля — отказ здесь, с названной причиной, а не тихий возврат
/// замера: этим путём идёт каждая xlsx-выгрузка в тестах.</para>
///
/// <para>Отдельным файлом — ради сторожа отказов (<c>DomainExceptionPolicyTests</c>): framework-отказ
/// здесь — несовместимость пакета, а исключение на весь экспортёр пропустило бы и отказы человеку.</para>
/// </summary>
internal static class StreamingSheetFonts
{
    private static readonly FieldInfo? Tracker =
        typeof(SXSSFSheet).GetField("_autoSizeColumnTracker", BindingFlags.Instance | BindingFlags.NonPublic);

    public static void Detach(SXSSFSheet sheet)
    {
        if (Tracker is null || Tracker.FieldType != typeof(Lazy<AutoSizeColumnTracker>))
            throw new InvalidOperationException(
                "В SXSSFSheet нет поля _autoSizeColumnTracker вида Lazy<AutoSizeColumnTracker>: NPOI сменил "
                + "устройство листа, и замер ширины системным шрифтом выключить нечем. "
                + "NPOI сменили вопреки решению остаться на ветке 2.7 (issue #1756): сначала решение о NPOI, "
                + "см. StreamingSheetFonts и NpoiLicenseTests.");
        Tracker.SetValue(sheet, new Lazy<AutoSizeColumnTracker>(() => null!));
    }
}
