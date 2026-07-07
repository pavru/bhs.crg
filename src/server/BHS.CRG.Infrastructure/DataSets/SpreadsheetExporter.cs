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

    public static (byte[] Bytes, string Extension, string ContentType) Export(
        SpreadsheetFormat format,
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<string?>> rows,
        string sheetName = "Данные") => format switch
    {
        SpreadsheetFormat.Csv => (Csv(columns, rows), "csv", "text/csv; charset=utf-8"),
        SpreadsheetFormat.Xls => (Workbook(new HSSFWorkbook(), columns, rows, sheetName), "xls", "application/vnd.ms-excel"),
        _ => (Workbook(new XSSFWorkbook(), columns, rows, sheetName), "xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
    };

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
        IReadOnlyList<IReadOnlyList<string?>> rows, string sheetName)
    {
        var sheet = wb.CreateSheet(SafeSheetName(sheetName));
        var header = sheet.CreateRow(0);
        for (var c = 0; c < columns.Count; c++) header.CreateCell(c).SetCellValue(columns[c]);
        for (var r = 0; r < rows.Count; r++)
        {
            var row = sheet.CreateRow(r + 1);
            for (var c = 0; c < columns.Count; c++)
                row.CreateCell(c).SetCellValue(c < rows[r].Count ? rows[r][c] ?? "" : "");
        }
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
