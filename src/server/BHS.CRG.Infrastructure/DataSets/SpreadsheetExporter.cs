using System.Globalization;
using System.Text;
using CsvHelper;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
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
        SpreadsheetFormat.Xls => (Workbook(new HSSFWorkbook(), columns, rows, sheetName, preamble), "xls",
            "application/vnd.ms-excel"),
        _ => (Workbook(new XSSFWorkbook(), columns, rows, sheetName, preamble), "xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
    };

    /// <summary>
    /// Один лист выгрузки. <paramref name="Preamble"/> — строки над таблицей: отчёт уходит из системы и
    /// живёт своей жизнью, а без них через неделю не понять, к чему он относился и насколько свеж.
    /// </summary>
    public record Sheet(
        string Name,
        IReadOnlyList<string> Columns,
        IReadOnlyList<IReadOnlyList<string?>> Rows,
        IReadOnlyList<string>? Preamble = null);

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

        IWorkbook wb = format == SpreadsheetFormat.Xls ? new HSSFWorkbook() : new XSSFWorkbook();
        foreach (var sheet in sheets) WriteSheet(wb, sheet);

        using var ms = new MemoryStream();
        wb.Write(ms, leaveOpen: true);
        return format == SpreadsheetFormat.Xls
            ? (ms.ToArray(), "xls", "application/vnd.ms-excel")
            : (ms.ToArray(), "xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    }

    private static void WriteSheet(IWorkbook wb, Sheet s)
    {
        var sheet = wb.CreateSheet(SafeSheetName(s.Name));
        var r = 0;

        foreach (var line in s.Preamble ?? [])
            sheet.CreateRow(r++).CreateCell(0).SetCellValue(line);
        if (s.Preamble is { Count: > 0 }) r++; // пустая строка между шапкой и таблицей

        var header = sheet.CreateRow(r++);
        for (var c = 0; c < s.Columns.Count; c++) header.CreateCell(c).SetCellValue(s.Columns[c]);

        foreach (var row in s.Rows)
        {
            var line = sheet.CreateRow(r++);
            for (var c = 0; c < s.Columns.Count; c++)
                line.CreateCell(c).SetCellValue(c < row.Count ? row[c] ?? "" : "");
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

    private static byte[] Workbook(IWorkbook wb, IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<string?>> rows, string sheetName,
        IReadOnlyList<string>? preamble = null)
    {
        // Один и тот же приём на одном листе и на многих: шапка пишется WriteSheet (issue #444), и
        // второй её экземпляр здесь разошёлся бы с ним при первой правке.
        WriteSheet(wb, new Sheet(sheetName, columns, rows, preamble));
        using var ms = new MemoryStream();
        wb.Write(ms, leaveOpen: true);
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
