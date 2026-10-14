using System.Text;
using BHS.CRG.Infrastructure.DataSets;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace BHS.CRG.Tests.DataSets;

public class SpreadsheetExporterTests
{
    private static readonly string[] Columns = ["Поз.", "Наименование", "Кол-во"];
    private static readonly IReadOnlyList<IReadOnlyList<string?>> Rows =
    [
        ["1", "Кабель ВВГнг", "120"],
        ["2", "Автомат АВДТ", null],
    ];

    [Theory]
    [InlineData("xlsx", SpreadsheetFormat.Xlsx)]
    [InlineData("xls", SpreadsheetFormat.Xls)]
    [InlineData("csv", SpreadsheetFormat.Csv)]
    [InlineData(null, SpreadsheetFormat.Xlsx)]   // по умолчанию — xlsx (приоритетный)
    [InlineData("XLSX", SpreadsheetFormat.Xlsx)] // регистронезависимо
    [InlineData("странное", SpreadsheetFormat.Xlsx)]
    public void ParseFormat_DefaultsToXlsx(string? input, SpreadsheetFormat expected)
        => Assert.Equal(expected, SpreadsheetExporter.ParseFormat(input));

    [Fact]
    public void Csv_HasHeaderAndRows_Utf8Bom()
    {
        var (bytes, ext, contentType) = SpreadsheetExporter.Export(SpreadsheetFormat.Csv, Columns, Rows);

        Assert.Equal("csv", ext);
        Assert.Contains("text/csv", contentType);
        // UTF-8 BOM (чтобы Excel открыл кириллицу корректно).
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("Поз.,Наименование,Кол-во", text);
        Assert.Contains("1,Кабель ВВГнг,120", text);
        Assert.Contains("2,Автомат АВДТ,", text); // null → пусто
    }

    [Fact]
    public void Xlsx_HasZipSignature()
    {
        var (bytes, ext, _) = SpreadsheetExporter.Export(SpreadsheetFormat.Xlsx, Columns, Rows);
        Assert.Equal("xlsx", ext);
        // XLSX = ZIP: сигнатура PK\x03\x04.
        Assert.Equal([(byte)'P', (byte)'K', 0x03, 0x04], bytes[..4]);
    }

    [Fact]
    public void Xls_HasOleSignature()
    {
        var (bytes, ext, _) = SpreadsheetExporter.Export(SpreadsheetFormat.Xls, Columns, Rows);
        Assert.Equal("xls", ext);
        // XLS = OLE2: сигнатура D0 CF 11 E0.
        Assert.Equal([0xD0, 0xCF, 0x11, 0xE0], bytes[..4]);
    }

    // ── Клетки своего вида и строки под таблицей (G3b, issue #1096) ─────────────────────────────────

    private static ISheet Written(SpreadsheetExporter.Sheet sheet)
    {
        var (bytes, _, _) = SpreadsheetExporter.ExportSheets(SpreadsheetFormat.Xlsx, [sheet]);
        return new XSSFWorkbook(new MemoryStream(bytes)).GetSheetAt(0);
    }

    [Fact]
    public void Number_and_date_are_written_as_values_not_as_text()
    {
        var sheet = Written(new("Счета", ["Номер", "Дата", "Сумма", "Дней", "Оплачен"],
            [["0012", new DateOnly(2026, 10, 14), 1234567.80m, 30m, true]]));

        var row = sheet.GetRow(1);
        // Номер «0012» — текст: угадай экспортёр в нём число, ведущие нули пропали бы молча.
        Assert.Equal(CellType.String, row.GetCell(0).CellType);
        Assert.Equal("0012", row.GetCell(0).StringCellValue);

        Assert.Equal(CellType.Numeric, row.GetCell(1).CellType);
        Assert.True(DateUtil.IsCellDateFormatted(row.GetCell(1)));
        Assert.Equal(new DateTime(2026, 10, 14), row.GetCell(1).DateCellValue);

        Assert.Equal(CellType.Numeric, row.GetCell(2).CellType);
        Assert.Equal(1234567.80, row.GetCell(2).NumericCellValue);
        Assert.Equal("#,##0.00", row.GetCell(2).CellStyle.GetDataFormatString());

        // Целое — без двух знаков: «30,00 дней» читалось бы как деньги.
        Assert.Equal(30, row.GetCell(3).NumericCellValue);
        Assert.NotEqual("#,##0.00", row.GetCell(3).CellStyle.GetDataFormatString());

        Assert.Equal(CellType.Boolean, row.GetCell(4).CellType);
    }

    [Fact]
    public void Absent_value_is_a_blank_cell_not_an_empty_string()
    {
        var sheet = Written(new("Счета", ["Сумма"], [[null], [10.5m]]));

        // Пустая строка в числовой колонке делает её для сводной таблицы текстовой.
        Assert.Equal(CellType.Blank, sheet.GetRow(1).GetCell(0).CellType);
    }

    [Fact]
    public void Footer_stands_under_the_table_after_an_empty_row()
    {
        var sheet = Written(new("Счета", ["Номер", "Сумма"], [["1", 10m], ["2", 20m]],
            Preamble: ["Отбор: всё"], Footer: [["Сумма", 30m]]));

        // Шапка, пустая, заголовок, две строки, ПУСТАЯ, итог.
        Assert.Equal("Номер", sheet.GetRow(2).GetCell(0).StringCellValue);
        Assert.Null(sheet.GetRow(5));
        Assert.Equal("Сумма", sheet.GetRow(6).GetCell(0).StringCellValue);
        Assert.Equal(30, sheet.GetRow(6).GetCell(1).NumericCellValue);
    }

    [Fact]
    public void Footer_row_may_be_wider_than_the_table()
    {
        var sheet = Written(new("Счета", ["Сумма"], [[10m]], Footer: [[10m, "Сумма"]]));

        Assert.Equal("Сумма", sheet.GetRow(3).GetCell(1).StringCellValue);
    }

    [Fact]
    public void Column_is_wide_enough_for_its_content()
    {
        var sheet = Written(new("Счета", ["Д", "Назначение"],
            [[new DateOnly(2026, 10, 14), new string('я', 200)]]));

        // Дата в колонке по умолчанию (8 знаков) показана решёткой — файл выглядит испорченным.
        Assert.True(sheet.GetColumnWidth(0) >= 10 * 256);
        // Потолок: одна длинная клетка не уводит остальные колонки за край экрана.
        Assert.True(sheet.GetColumnWidth(1) <= 62 * 256);
    }

    [Fact]
    public void Styles_are_shared_by_cells_not_created_per_cell()
    {
        var rows = Enumerable.Range(0, 5000)
            .Select(i => (IReadOnlyList<object?>)[new DateOnly(2026, 1, 1).AddDays(i), i + 0.5m]).ToList();
        var (bytes, _, _) = SpreadsheetExporter.ExportSheets(SpreadsheetFormat.Xls, [new("Счета", ["Дата", "Сумма"], rows)]);

        // У XLS стилей около четырёх тысяч на книгу: стиль на клетку упал бы здесь исключением.
        Assert.True(new NPOI.HSSF.UserModel.HSSFWorkbook(new MemoryStream(bytes)).NumCellStyles < 100);
    }
}
