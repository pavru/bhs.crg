using System.Globalization;
using System.Text;
using CsvHelper;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using NPOI.XSSF.Streaming;
using NPOI.XSSF.UserModel;

namespace BHS.CRG.Infrastructure.DataSets;

public enum SpreadsheetFormat { Csv, Xls, Xlsx }

/// <summary>
/// Чистая выгрузка табличных данных (колонки + строки) в CSV / XLS / XLSX. XLSX — приоритетный
/// формат (см. запрос на экспорт спецификаций/кабельных журналов). CSV — через уже имеющийся
/// CsvHelper (UTF-8 с BOM, чтобы кириллица корректно открывалась в Excel); XLS/XLSX — через NPOI
/// (одна библиотека на оба формата: HSSF — .xls, XSSF — .xlsx).
/// </summary>
public static class SpreadsheetExporter
{
    public static SpreadsheetFormat ParseFormat(string? format) => (format ?? "").Trim().ToLowerInvariant() switch
    {
        "csv" => SpreadsheetFormat.Csv,
        "xls" => SpreadsheetFormat.Xls,
        _ => SpreadsheetFormat.Xlsx, // xlsx — по умолчанию (приоритетный)
    };

    /// <param name="preamble">
    /// Строки НАД таблицей — тем же приёмом, что у многолистового отчёта (<see cref="Sheet" />). Сюда
    /// идёт граница выдачи опубликованного набора (ТЗ CORE-24.3, issue #965): выгрузка уходит из
    /// системы и живёт своей жизнью, а «что это за строки и все ли они» по самому файлу не узнать.
    /// ⚠️ <b>У CSV шапки НЕТ.</b> Своего места под примечание формат не предусматривает, и строка
    /// легла бы первой записью файла, сдвинув заголовок колонок на вторую, — а такой файл заново
    /// загружают в систему как набор данных, и разбор увидел бы в заголовке подпись вместо имён
    /// колонок (нашло ревью PR #1057). ТЗ CORE-24.3 требует подпись в XLSX, и здесь это буквально:
    /// у XLSX шапка есть, у CSV — нет. Выгрузку в CSV поэтому подписывает не файл, а экран, с
    /// которого её забирают.
    /// </param>
    public static (byte[] Bytes, string Extension, string ContentType) Export(
        SpreadsheetFormat format,
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<string?>> rows,
        string sheetName = "Данные",
        IReadOnlyList<string>? preamble = null) => format switch
    {
        SpreadsheetFormat.Csv => (Csv(columns, rows), "csv", "text/csv; charset=utf-8"),
        // Один и тот же приём на одном листе и на многих: шапка пишется WriteSheet (issue #444), и
        // второй её экземпляр здесь разошёлся бы с ним при первой правке.
        _ => ExportSheets(format, [new Sheet(sheetName, columns, rows, preamble)]),
    };

    /// <summary>
    /// Один лист выгрузки. <paramref name="Preamble"/> — строки над таблицей: отчёт уходит из системы и
    /// живёт своей жизнью, а без них через неделю не понять, к чему он относился и насколько свеж.
    ///
    /// <para><b>Клетка несёт значение, а не его запись</b> (G3b, issue #1096): число ложится числом,
    /// дата — датой, остальное — текстом. Сумма, выгруженная текстом, в Excel не складывается и не
    /// сортируется, а дата текстом сортируется по первому знаку дня — и оба отказа выглядят как
    /// обычный лист. Виды клеток — в <see cref="WriteCell" />.</para>
    /// </summary>
    /// <param name="Footer">Строки ПОД таблицей, через пустую строку от неё: итоги. Пустая строка —
    /// не украшение: без неё автофильтр и сводная таблица Excel приняли бы итог за ещё одну строку
    /// данных и посчитали бы его дважды.</param>
    public record Sheet(
        string Name,
        IReadOnlyList<string> Columns,
        IReadOnlyList<IReadOnlyList<object?>> Rows,
        IReadOnlyList<string>? Preamble = null,
        IReadOnlyList<IReadOnlyList<object?>>? Footer = null);

    /// <summary>
    /// Многолистовая выгрузка (issue #444). Вкладки отвечают на разные вопросы — находки арифметики и
    /// утверждения агента, — и сваливать их на один лист значило бы выдать одно за другое.
    ///
    /// Только XLS/XLSX: у CSV вкладок нет, и молча склеить их в один файл — потерять эту границу.
    /// </summary>
    public static (byte[] Bytes, string Extension, string ContentType) ExportSheets(
        SpreadsheetFormat format, IReadOnlyList<Sheet> sheets)
    {
        if (format == SpreadsheetFormat.Csv)
            throw new InvalidRequestException("CSV не поддерживает вкладки — выгрузите отчёт в XLSX.");

        // XLSX пишется ПОТОКОМ: строки, ушедшие из окна, сбрасываются во временный файл, а не
        // копятся в памяти. Замер на 50 000 строк по 20 колонок (G3b, issue #1096): обычная книга —
        // 3 с и 650 МБ на пике, потоковая — 2 с и 250 МБ. Плата — временный файл: его убирает
        // Dispose, поэтому книга живёт в using, а не до сборщика мусора.
        using var streamed = format == SpreadsheetFormat.Xls ? null : new SXSSFWorkbook(new XSSFWorkbook(), StreamWindow);
        IWorkbook wb = streamed is not null ? streamed : new HSSFWorkbook();
        foreach (var sheet in sheets) WriteSheet(wb, sheet);

        using var ms = new MemoryStream();
        wb.Write(ms, leaveOpen: true);
        return format == SpreadsheetFormat.Xls
            ? (ms.ToArray(), "xls", "application/vnd.ms-excel")
            : (ms.ToArray(), "xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    }

    /// <summary>Сколько последних строк листа потоковая книга держит в памяти.</summary>
    private const int StreamWindow = 200;

    private static void WriteSheet(IWorkbook wb, Sheet s)
    {
        var sheet = wb.CreateSheet(SafeSheetName(s.Name));
        var styles = new CellStyles(wb);
        var widths = new int[s.Columns.Count];
        var r = 0;

        foreach (var line in s.Preamble ?? [])
            sheet.CreateRow(r++).CreateCell(0).SetCellValue(line);
        if (s.Preamble is { Count: > 0 }) r++; // пустая строка между шапкой и таблицей

        var header = sheet.CreateRow(r++);
        for (var c = 0; c < s.Columns.Count; c++)
        {
            header.CreateCell(c).SetCellValue(s.Columns[c]);
            widths[c] = s.Columns[c].Length;
        }

        foreach (var row in s.Rows) WriteRow(sheet.CreateRow(r++), row, s.Columns.Count, styles, widths);

        if (s.Footer is { Count: > 0 })
        {
            r++; // пустая строка между таблицей и итогами — см. Sheet.Footer
            // Строка итогов может быть шире таблицы: её подпись встаёт за последней колонкой, когда
            // под первой стоит сам итог.
            foreach (var row in s.Footer) WriteRow(sheet.CreateRow(r++), row, row.Count, styles, widths);
        }

        // Ширина — по содержимому, счётом знаков, а не AutoSizeColumn: тот меряет шрифтом, которого в
        // контейнере поставки нет, и на длинном листе обходит каждую клетку второй раз. Без ширины
        // вовсе дата в колонке по умолчанию показана решёткой «########» — значение на месте, но
        // человек видит испорченный файл.
        for (var c = 0; c < widths.Length; c++)
            sheet.SetColumnWidth(c, (Math.Clamp(widths[c], MinWidth, MaxWidth) + 2) * 256);
    }

    private const int MinWidth = 6;
    private const int MaxWidth = 60;

    private static void WriteRow(
        IRow line, IReadOnlyList<object?> row, int count, CellStyles styles, int[] widths)
    {
        for (var c = 0; c < count; c++)
        {
            var shown = WriteCell(line.CreateCell(c), c < row.Count ? row[c] : null, styles);
            if (c < widths.Length && shown > widths[c]) widths[c] = shown;
        }
    }

    /// <summary>
    /// Значение → клетка своего вида; возвращает, сколько знаков клетка займёт на листе.
    ///
    /// <para>Число — числом (деньги, то есть дробь до сотых, — с разделителем разрядов и двумя
    /// знаками), <see cref="DateOnly" /> — датой дня, <see cref="DateTime" /> — датой со временем,
    /// флаг — логическим значением, <c>null</c> — пустой клеткой, а не пустой строкой: пустая строка в
    /// числовой колонке делает её для сводной таблицы текстовой. Всё прочее — текстом.</para>
    ///
    /// <para>⚠️ Строка, похожая на число, остаётся ТЕКСТОМ, и это нарочно: номер счёта «0012» и ИНН
    /// не числа, а угадывание превратило бы их в 12 и в 7.7E+9. Видом клетки распоряжается тот, кто
    /// знает вид колонки, — он и кладёт сюда decimal, а не строку.</para>
    /// </summary>
    private static int WriteCell(ICell cell, object? value, CellStyles styles)
    {
        switch (value)
        {
            case null:
                return 0;
            case string text:
                cell.SetCellValue(text);
                return text.Length;
            case bool flag:
                cell.SetCellValue(flag);
                return 6; // «ИСТИНА»
            case DateOnly day:
                cell.SetCellValue(day.ToDateTime(TimeOnly.MinValue));
                cell.CellStyle = styles.Day;
                return 10;
            case DateTime moment:
                cell.SetCellValue(moment);
                cell.CellStyle = styles.Moment;
                return 16;
            case decimal number:
                cell.SetCellValue((double)number);
                // Дробь до сотых — деньги: «1 234 567,80», а не «1234567.8». Целое и дробь длиннее
                // сотых (количество, коэффициент) остаются как есть: два знака их округлили бы на виду.
                var money = number.Scale is 1 or 2;
                if (money) cell.CellStyle = styles.Money;
                var digits = decimal.Truncate(Math.Abs(number)).ToString(CultureInfo.InvariantCulture).Length;
                return money ? digits + (digits - 1) / 3 + 4 : number.ToString(CultureInfo.InvariantCulture).Length;
            case sbyte or byte or short or ushort or int or uint or long or ulong or float or double:
                var real = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                cell.SetCellValue(real);
                return real.ToString(CultureInfo.InvariantCulture).Length;
            default:
                var other = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                cell.SetCellValue(other);
                return other.Length;
        }
    }

    /// <summary>
    /// Стили клеток листа. Заводятся по одному на книгу и по требованию: стиль на клетку упёрся бы в
    /// потолок числа стилей формата (у XLS — около четырёх тысяч) на первой же длинной выгрузке.
    /// </summary>
    private sealed class CellStyles(IWorkbook wb)
    {
        private ICellStyle? _day, _moment, _money;

        public ICellStyle Day => _day ??= Of("dd.mm.yyyy");
        public ICellStyle Moment => _moment ??= Of("dd.mm.yyyy hh:mm");
        public ICellStyle Money => _money ??= Of("#,##0.00");

        private ICellStyle Of(string format)
        {
            var style = wb.CreateCellStyle();
            style.DataFormat = wb.CreateDataFormat().GetFormat(format);
            return style;
        }
    }

    private static byte[] Csv(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string?>> rows)
    {
        using var ms = new MemoryStream();
        using (var writer = new StreamWriter(ms, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
        using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
        {
            foreach (var c in columns) csv.WriteField(c);
            csv.NextRecord();
            foreach (var row in rows)
            {
                for (var i = 0; i < columns.Count; i++)
                    csv.WriteField(i < row.Count ? row[i] ?? "" : "");
                csv.NextRecord();
            }
        }
        return ms.ToArray();
    }

    /// <summary>Имя листа Excel: ≤31 символа, без запрещённых символов <c>: \ / ? * [ ]</c>.</summary>
    private static string SafeSheetName(string name)
    {
        var cleaned = new string(name.Select(ch => ":\\/?*[]".Contains(ch) ? ' ' : ch).ToArray()).Trim();
        if (cleaned.Length == 0) cleaned = "Данные";
        return cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }
}
