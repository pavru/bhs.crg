using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Modules.Ports;

public sealed class ModuleSpreadsheetsPort : IModuleSpreadsheets
{
    public byte[] Xlsx(ModuleSpreadsheet sheet)
    {
        var rows = sheet.Rows.Select(row => (IReadOnlyList<object?>)row.Select(cell =>
            cell is ModuleSpreadsheetMoney money ? (object)new SpreadsheetExporter.Money(money.Value) : cell).ToArray()).ToArray();
        return SpreadsheetExporter.ExportSheets(SpreadsheetFormat.Xlsx,
            [new SpreadsheetExporter.Sheet(sheet.Name, sheet.Columns, rows, sheet.Preamble)]).Bytes;
    }
}
