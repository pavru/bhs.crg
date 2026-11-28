namespace BHS.CRG.Modules.Ports;

/// <summary>Числовая денежная клетка с форматом суммы в общей выгрузке XLSX.</summary>
public readonly record struct ModuleSpreadsheetMoney(decimal Value);

/// <summary>Готовые разрешённые данные листа. Права, отбор и полноту определяет модуль-потребитель.</summary>
public sealed record ModuleSpreadsheet(
    string Name, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows,
    IReadOnlyList<string> Preamble);

/// <summary>Общий экспортёр XLSX для модулей, без зависимости от инфраструктуры ядра.</summary>
public interface IModuleSpreadsheets
{
    byte[] Xlsx(ModuleSpreadsheet sheet);
}
