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
            [["0012", new DateOnly(2026, 10, 14), new SpreadsheetExporter.Money(1234567.80m), 30m, true]]));

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

        Assert.Equal(30, row.GetCell(3).NumericCellValue);

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
            .Select(i => (IReadOnlyList<object?>)[new DateOnly(2026, 1, 1).AddDays(i), new SpreadsheetExporter.Money(i + 0.5m)]).ToList();
        var (bytes, _, _) = SpreadsheetExporter.ExportSheets(SpreadsheetFormat.Xls, [new("Счета", ["Дата", "Сумма"], rows)]);

        // У XLS стилей около четырёх тысяч на книгу: стиль на клетку упал бы здесь исключением.
        Assert.True(new NPOI.HSSF.UserModel.HSSFWorkbook(new MemoryStream(bytes)).NumCellStyles < 100);
    }

    // ── Ревью PR #1325 ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Money_is_named_by_the_caller_not_guessed_from_the_number()
    {
        var sheet = Written(new("Счета", ["Количество", "Сумма"],
            [[2.5m, new SpreadsheetExporter.Money(100m / 3m)], [30m, new SpreadsheetExporter.Money(5m)]]));

        // Количество 2,5 — не сумма, а сумма 33,3333… и сумма 5 — обе суммы: формат один на колонку.
        Assert.NotEqual("#,##0.00", sheet.GetRow(1).GetCell(0).CellStyle.GetDataFormatString());
        Assert.Equal("#,##0.00", sheet.GetRow(1).GetCell(1).CellStyle.GetDataFormatString());
        Assert.Equal("#,##0.00", sheet.GetRow(2).GetCell(1).CellStyle.GetDataFormatString());
    }

    [Fact]
    public void Moment_with_offset_is_a_date_cell_with_its_own_clock()
    {
        var sheet = Written(new("Счета", ["Создан", "Время"],
            [[new DateTimeOffset(2026, 10, 14, 9, 30, 0, TimeSpan.FromHours(3)), new TimeOnly(9, 5)]]));

        var cell = sheet.GetRow(1).GetCell(0);
        Assert.True(DateUtil.IsCellDateFormatted(cell));
        Assert.Equal(new DateTime(2026, 10, 14, 9, 30, 0), cell.DateCellValue);
        Assert.Equal("09:05", sheet.GetRow(1).GetCell(1).StringCellValue);
    }

    [Fact]
    public void Date_excel_cannot_hold_is_written_as_text()
    {
        var sheet = Written(new("Счета", ["Дата"], [[default(DateOnly)], [new DateOnly(1812, 9, 7)]]));

        // Клеткой-датой она стала бы решёткой либо ошибкой: значение неверное, а вид — испорченного файла.
        Assert.Equal("01.01.0001", sheet.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal("07.09.1812", sheet.GetRow(2).GetCell(0).StringCellValue);
    }

    [Fact]
    public void Integer_a_number_cell_cannot_hold_is_written_as_text()
    {
        var sheet = Written(new("Счета", ["Код", "Малое", "Дробь"], [[9007199254740993L, 42L, 0.1f]]));

        // Числом оно легло бы как …992 — другой код без единого признака отказа.
        Assert.Equal("9007199254740993", sheet.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal(42, sheet.GetRow(1).GetCell(1).NumericCellValue);
        Assert.Equal(0.1, sheet.GetRow(1).GetCell(2).NumericCellValue);
    }

    [Fact]
    public void Long_xlsx_keeps_every_row_the_footer_and_the_widths()
    {
        // Длиннее окна потоковой книги: первые строки к концу записи уже сброшены во временный файл.
        var rows = Enumerable.Range(0, 1000).Select(i => (IReadOnlyList<object?>)
            [$"Счёт № {i}", new DateOnly(2026, 1, 1).AddDays(i), new SpreadsheetExporter.Money(i + 0.5m), null]).ToList();
        var sheet = Written(new("Счета", ["Номер", "Дата", "Сумма", "Пусто"], rows,
            Preamble: ["Отбор: всё"], Footer: [[null, null, new SpreadsheetExporter.Money(500000m)]]));

        Assert.Equal("Счёт № 0", sheet.GetRow(3).GetCell(0).StringCellValue);
        Assert.Equal(new DateTime(2026, 1, 1), sheet.GetRow(3).GetCell(1).DateCellValue);
        Assert.Equal("Счёт № 999", sheet.GetRow(1002).GetCell(0).StringCellValue);
        Assert.Equal(999.5, sheet.GetRow(1002).GetCell(2).NumericCellValue);
        Assert.Null(sheet.GetRow(1003));
        Assert.Equal(500000, sheet.GetRow(1004).GetCell(2).NumericCellValue);
        Assert.True(sheet.GetColumnWidth(1) >= 10 * 256);
    }

    [Fact]
    public void Footer_beyond_the_table_gets_its_width_too()
    {
        var sheet = Written(new("Счета", ["Номер"], [["1"]],
            Footer: [["Итого", null, new SpreadsheetExporter.Money(-1234567.80m)]]));

        // «-1 234 567,80» — 13 знаков; при ширине по умолчанию сумма показана решёткой.
        Assert.True(sheet.GetColumnWidth(2) >= 13 * 256);
    }

    [Fact]
    public void Number_wider_than_general_format_does_not_stretch_the_column()
    {
        var sheet = Written(new("Счета", ["Доля"], [[100m / 3m]]));

        Assert.True(sheet.GetColumnWidth(0) <= 13 * 256);
    }

    [Fact]
    public void Text_sheet_keeps_empty_string_cells()
    {
        var (bytes, _, _) = SpreadsheetExporter.Export(SpreadsheetFormat.Xlsx, Columns, Rows);
        var sheet = new XSSFWorkbook(new MemoryStream(bytes)).GetSheetAt(0);

        // Пустая клетка дала бы длинному тексту соседа разлиться поверх неё.
        Assert.Equal(CellType.String, sheet.GetRow(2).GetCell(2).CellType);
    }

    [Fact]
    public void Styles_are_shared_by_sheets_of_one_book()
    {
        var sheets = Enumerable.Range(0, 20).Select(i => new SpreadsheetExporter.Sheet(
            $"Лист {i}", ["Дата", "Сумма"], [[new DateOnly(2026, 1, 1), new SpreadsheetExporter.Money(1m)]])).ToList();
        var (bytes, _, _) = SpreadsheetExporter.ExportSheets(SpreadsheetFormat.Xlsx, sheets);

        Assert.True(new XSSFWorkbook(new MemoryStream(bytes)).NumCellStyles < 10);
    }
}
