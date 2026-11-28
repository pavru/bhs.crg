using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Costs.Endpoints;

internal static class EstimateVsCostsExport
{
    internal static ModuleSpreadsheet Sheet(EstimateVsCostsView report)
    {
        var estimate = report.EstimateState == "shown";
        List<string> columns = ["Материал"];
        if (estimate) columns.Add($"Смета без НДС, {report.EstimateCurrency}");
        columns.AddRange([$"Разнесено без НДС, {report.FactCurrency}", $"Оплачено без НДС, {report.FactCurrency}"]);
        if (estimate) columns.Add("Отклонение" + (report.CurrencyMismatch ? "" : $", {report.FactCurrency}"));
        columns.Add("Пояснение");
        var rows = report.Rows.Select(row =>
        {
            List<object?> cells = [row.Name];
            if (estimate) cells.Add(Money(row.Estimate.Amount));
            cells.Add(Money(row.Allocated));
            cells.Add(Money(row.Paid));
            if (estimate) cells.Add(Money(row.Difference));
            List<string> notes = [];
            if (row.AllocatedVatUnknown) notes.Add("Разнесено — НДС неизвестен, учтено полной суммой.");
            if (row.PaidVatUnknown) notes.Add("Оплачено — НДС неизвестен, учтено полной суммой.");
            if (row.AllocatedIncomplete) notes.Add("Разнесено — известная часть. Стоимость остальных долей неизвестна.");
            if (row.PaidIncomplete) notes.Add("Оплачено — известная часть. Стоимость остальных долей неизвестна.");
            if (estimate && row.Estimate.State == "not-in-estimate") notes.Add("Нет в смете.");
            if (row.Estimate.BasisUnknown) notes.Add("Основа сметной суммы без НДС не подтверждена.");
            cells.Add(string.Join(" ", notes));
            return (IReadOnlyList<object?>)cells;
        }).ToArray();
        List<string> preamble = [
            "План против факта материалов",
            report.Construction + (report.Section is null ? " — все разделы" : " — " + report.Section),
            "За всё время, с исправлениями. Сравнение по разнесённому без НДС.",
        ];
        if (report.Message is not null) preamble.Add(report.Message);
        if (report.Undecided > 0) preamble.Add($"Строк сметы без решения о разделе — {report.Undecided}.");
        return new("План против факта", columns, rows, preamble);
    }

    private static object? Money(decimal? amount) =>
        amount is { } value ? new ModuleSpreadsheetMoney(value) : null;
}
