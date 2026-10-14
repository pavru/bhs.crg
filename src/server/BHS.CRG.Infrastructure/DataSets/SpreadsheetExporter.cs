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
        _ => ExportSheets(format, [new Sheet(sheetName, columns, rows, preamble, EmptyAsText: true)]),
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
    /// <param name="EmptyAsText">Отсутствующее значение — клеткой с пустой строкой, а не пустой клеткой.
    /// Так пишутся ТЕКСТОВЫЕ листы — выгрузка источника набора, отчёт сверки: длинный текст соседней
    /// клетки Excel разливает поверх пустых, и лист с пропусками читался бы как съехавший (ревью
    /// PR #1325). Листу с числами нужно обратное: пустая строка в числовой колонке делает её для
    /// сводной таблицы текстовой.</param>
    public record Sheet(
        string Name,
        IReadOnlyList<string> Columns,
        IReadOnlyList<IReadOnlyList<object?>> Rows,
        IReadOnlyList<string>? Preamble = null,
        IReadOnlyList<IReadOnlyList<object?>>? Footer = null,
        bool EmptyAsText = false);

    /// <summary>
    /// Сумма денег: клетка числом в формате «1 234 567,80». Деньгами значение называет тот, кто знает
    /// колонку, — и называет ВСЮ колонку. Угадывать по самому числу нельзя: количество 2,5 оделось бы
    /// суммой, а сумма 33,3333 рядом с 33,33 осталась бы без формата — в одной колонке (ревью PR #1325).
    /// </summary>
    public readonly record struct Money(decimal Value);

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
        var styles = new CellStyles(wb);
        foreach (var sheet in sheets) WriteSheet(wb, sheet, styles);

        using var ms = new MemoryStream();
        wb.Write(ms, leaveOpen: true);
        return format == SpreadsheetFormat.Xls
            ? (ms.ToArray(), "xls", "application/vnd.ms-excel")
            : (ms.ToArray(), "xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    }

    /// <summary>Сколько последних строк листа потоковая книга держит в памяти.</summary>
    private const int StreamWindow = 200;

    private static void WriteSheet(IWorkbook wb, Sheet s, CellStyles styles)
    {
        var sheet = wb.CreateSheet(SafeSheetName(s.Name));
        // Строка итогов может быть шире таблицы: её подпись встаёт за последней колонкой, когда под
        // первой стоит сам итог. Ширину считаем и там — иначе сумма за краем таблицы показана решёткой.
        var widths = new int[Math.Max(s.Columns.Count, (s.Footer ?? []).Select(f => f.Count).DefaultIfEmpty().Max())];
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

        foreach (var row in s.Rows)
            WriteRow(sheet.CreateRow(r++), row, s.Columns.Count, styles, widths, s.EmptyAsText);

        if (s.Footer is { Count: > 0 })
        {
            r++; // пустая строка между таблицей и итогами — см. Sheet.Footer
            foreach (var row in s.Footer) WriteRow(sheet.CreateRow(r++), row, row.Count, styles, widths, false);
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
        IRow line, IReadOnlyList<object?> row, int count, CellStyles styles, int[] widths, bool emptyAsText)
    {
        for (var c = 0; c < count; c++)
        {
            var value = c < row.Count ? row[c] : null;
            var shown = WriteCell(line.CreateCell(c), value ?? (emptyAsText ? "" : null), styles);
            if (shown > widths[c]) widths[c] = shown;
        }
    }

    /// <summary>
    /// Значение → клетка своего вида; возвращает, сколько знаков клетка займёт на листе.
    ///
    /// <para>Число — числом, <see cref="Money" /> — числом в формате денег, <see cref="DateOnly" /> —
    /// датой дня, момент — датой со временем, флаг — логическим значением, <c>null</c> — пустой
    /// клеткой. Всё прочее — текстом.</para>
    ///
    /// <para>⚠️ Строка, похожая на число, остаётся ТЕКСТОМ, и это нарочно: номер счёта «0012» и ИНН
    /// не числа, а угадывание превратило бы их в 12 и в 7.7E+9. Видом клетки распоряжается тот, кто
    /// знает вид колонки, — он и кладёт сюда decimal, а не строку.</para>
    ///
    /// <para>⚠️ <b>Часовой пояс здесь не переводится.</b> Момент ложится теми часами, которые в нём
    /// записаны: у <see cref="DateTimeOffset" /> — часами его собственного смещения. Пояса читающего
    /// экспортёр не знает; перевести момент обязан кладущий.</para>
    ///
    /// <para><b>Что клетка-число удержать не может, ложится текстом</b> — целое длиннее 2^53 и дата
    /// раньше 1900 года. Число, молча потерявшее младшие разряды, и дата решёткой выглядят обычным
    /// листом; текст хотя бы показывает то, что было. У <c>decimal</c> предел тот же, что у самого
    /// Excel, — 15 значащих цифр.</para>
    /// </summary>
    private static int WriteCell(ICell cell, object? value, CellStyles styles)
    {
        switch (value)
        {
            case null:
                return 0;
            case string text:
                return Text(cell, text);
            case bool flag:
                cell.SetCellValue(flag);
                return 6; // «ИСТИНА»
            case DateOnly day:
                return Dated(cell, day.ToDateTime(TimeOnly.MinValue), styles.Day, "dd.MM.yyyy");
            case DateTime moment:
                return Dated(cell, moment, styles.Moment, "dd.MM.yyyy HH:mm");
            case DateTimeOffset moment:
                return Dated(cell, moment.DateTime, styles.Moment, "dd.MM.yyyy HH:mm");
            case TimeOnly time:
                return Text(cell, time.ToString("HH:mm", CultureInfo.InvariantCulture));
            case Money money:
                cell.SetCellValue((double)money.Value);
                cell.CellStyle = styles.Money;
                // Знаки целой части, пробелы разрядов, «,00» и минус.
                var digits = decimal.Truncate(Math.Abs(money.Value)).ToString(CultureInfo.InvariantCulture).Length;
                return digits + (digits - 1) / 3 + 3 + (money.Value < 0 ? 1 : 0);
            case decimal number:
                return Number(cell, (double)number);
            case long big when Math.Abs((decimal)big) > MaxExactInteger:
                return Text(cell, big.ToString(CultureInfo.InvariantCulture));
            case ulong big when big > MaxExactInteger:
                return Text(cell, big.ToString(CultureInfo.InvariantCulture));
            // Через запись, а не приведением: (double)0.1f — это 0.10000000149011612.
            case float single:
                return Number(cell,
                    double.Parse(single.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
            case sbyte or byte or short or ushort or int or uint or long or ulong or double:
                return Number(cell, Convert.ToDouble(value, CultureInfo.InvariantCulture));
            default:
                return Text(cell, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
        }
    }

    /// <summary>Целое, которое число двойной точности ещё хранит без потери: 2^53.</summary>
    private const long MaxExactInteger = 9_007_199_254_740_992;

    /// <summary>Сколько знаков показывает клетка общего формата: число длиннее Excel округляет на виду.</summary>
    private const int GeneralWidth = 11;

    private static int Text(ICell cell, string text)
    {
        cell.SetCellValue(text);
        return text.Length;
    }

    private static int Number(ICell cell, double number)
    {
        cell.SetCellValue(number);
        return Math.Min(number.ToString(CultureInfo.InvariantCulture).Length, GeneralWidth);
    }

    /// <summary>Дата клеткой-датой; раньше 1900 года — текстом в той же записи: такой даты у Excel нет.</summary>
    private static int Dated(ICell cell, DateTime moment, ICellStyle style, string written)
    {
        if (!DateUtil.IsValidExcelDate(DateUtil.GetExcelDate(moment)))
            return Text(cell, moment.ToString(written, CultureInfo.InvariantCulture));
        cell.SetCellValue(moment);
        cell.CellStyle = style;
        return written.Length;
    }

    /// <summary>
    /// Стили клеток. Заводятся по одному на КНИГУ, а не на лист, и по требованию: стиль на клетку упёрся бы в
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
