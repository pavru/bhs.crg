using System.Text;
using BHS.CRG.Infrastructure.DataSets;

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
}
