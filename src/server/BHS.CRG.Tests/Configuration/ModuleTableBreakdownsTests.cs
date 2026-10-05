using BHS.CRG.Api.Modules.Tables;
using BHS.CRG.Application.Tables;
using BHS.CRG.Modules.Tables;
using Microsoft.Extensions.Logging.Abstractions;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Расшифровка строки на пути от службы модуля к ответу (задача G4, issue #1097): что ядро вычищает, что
/// считает само и чем отвечает на расшифровку, не сошедшуюся со своей строкой.
/// </summary>
public class ModuleTableBreakdownsTests
{
    private static readonly ModuleTable Table = new(
        "docs", "Документы", "документ", "probe", ModuleTableIsolation.None, "Отдаёт всё",
        [
            new("Номер", "Номер", ModuleTableColumnKind.Text),
            new("Сумма", "Сумма", ModuleTableColumnKind.Number, "probe.money", "суммы", DependsOnFilter: true),
            new("Итого", "Итого", ModuleTableColumnKind.Number, "probe.money", "суммы"),
        ],
        typeof(ProbeRows),
        Breakdown: new("Разноска",
            [
                new("Объект", "Объект", ModuleTableColumnKind.Text),
                new("Доля", "Доля", ModuleTableColumnKind.Number, Follows: "Сумма"),
            ],
            [new("Доля", Named: "Сумма", Whole: "Итого")]));

    private static readonly ModuleTableQuery One = new(new HashSet<string>(), Guid.Empty, Row: "1");

    /// <summary>
    /// Сумма расшифровки не сошлась с клеткой — блок не показан и сказано почему. Не исключение: оно
    /// унесло бы и поля строки, а расхождение может всплыть на данных, которых тесты не видели.
    /// </summary>
    [Fact]
    public void Расшифровка_не_сошедшаяся_со_строкой_отвечает_отказом_а_не_второй_цифрой()
    {
        var page = Page(cell: 50m, total: 100m, narrowed: true, Row("А", 40m, named: true), Row("Б", 60m));

        var built = ModuleTableBreakdowns.Build(Table, One, page, Columns(), Open("Сумма", "Итого"), NullLogger.Instance)!;

        Assert.Equal(ModuleTableBreakdowns.Mismatch, built.Refusal);
        Assert.Empty(built.Rows);
        Assert.Empty(built.Columns);
        Assert.Empty(built.Totals);
    }

    [Fact]
    public void Суммы_считает_ядро_названное_и_целое()
    {
        var page = Page(cell: 40m, total: 100m, narrowed: true, Row("А", 40m, named: true), Row("Б", 60m));

        var built = ModuleTableBreakdowns.Build(Table, One, page, Columns(), Open("Сумма", "Итого"), NullLogger.Instance)!;

        Assert.Null(built.Refusal);
        Assert.Equal(new TableBreakdownTotalDto("Доля", 100m, 40m), Assert.Single(built.Totals));
        Assert.Equal([true, false], built.Rows.Select(r => r.Named));
    }

    /// <summary>«Денег нет» и «ноль» — одно число: счёт с нулевой суммой панель не роняет (ревью PR #1197).</summary>
    [Fact]
    public void Нулевая_сумма_строки_сходится_с_расшифровкой_без_чисел()
    {
        var page = Page(cell: 0m, total: 0m, narrowed: false, Row("А", null));

        var built = ModuleTableBreakdowns.Build(Table, One, page, Columns(), Open("Сумма", "Итого"), NullLogger.Instance)!;

        Assert.Null(built.Refusal);
        Assert.Single(built.Rows);
    }

    /// <summary>
    /// Кому суммы закрыты: значений колонки нет, итогов нет, подписи модуля нет — и нет строки, которая
    /// существует только из-за денег: её название само было бы фактом о суммах.
    /// </summary>
    [Fact]
    public void Закрытые_суммы_уносят_значения_итоги_подпись_и_денежную_строку()
    {
        var page = Page(cell: null, total: null, narrowed: false,
            Row("А", 40m), Row("Не разнесено", 60m) with { Follows = "Сумма" }) with { };
        var closed = Columns("no-right", "нет права на суммы");

        var built = ModuleTableBreakdowns.Build(Table, One, WithoutCells(page), closed, Open(), NullLogger.Instance)!;

        var row = Assert.Single(built.Rows);
        Assert.Equal("А", row.Values["Объект"]);
        Assert.False(row.Values.ContainsKey("Доля"));
        Assert.Empty(built.Totals);
        Assert.Null(built.Note);
        Assert.Equal("нет права на суммы", built.Columns.Single(c => c.Key == "Доля").Reason);
    }

    [Fact]
    public void Нарушение_формы_расшифровки_ошибка_модуля()
    {
        var page = Page(cell: 100m, total: 100m, narrowed: false, Row("А", 100m));

        // Расшифровка со страницей, а не с одной строкой.
        Assert.Contains("запрошена страница", Assert.Throws<InvalidOperationException>(() =>
            ModuleTableBreakdowns.Build(Table, One with { Row = null }, page, Columns(), Open("Сумма", "Итого"), NullLogger.Instance)).Message);

        // Незнакомый ключ и не тот вид значения.
        var alien = page with { Breakdown = new([new(new Dictionary<string, object?> { ["Раздел"] = "АР" })], false) };
        Assert.Contains("колонки «Раздел» в объявлении нет", Assert.Throws<InvalidOperationException>(() =>
            ModuleTableBreakdowns.Build(Table, One, alien, Columns(), Open("Сумма", "Итого"), NullLogger.Instance)).Message);
        var text = page with { Breakdown = new([new(new Dictionary<string, object?> { ["Доля"] = "сорок" })], false) };
        Assert.Contains("в колонке «Доля» лежит не", Assert.Throws<InvalidOperationException>(() =>
            ModuleTableBreakdowns.Build(Table, One, text, Columns(), Open("Сумма", "Итого"), NullLogger.Instance)).Message);
    }

    private static TableBreakdownRow Row(string label, decimal? share, bool named = false) => new(
        new Dictionary<string, object?> { ["Объект"] = label, ["Доля"] = share }, named);

    private static ModuleTablePage Page(decimal? cell, decimal? total, bool narrowed, params TableBreakdownRow[] rows) => new(
        [new Dictionary<string, object?> { ["Номер"] = "1", ["Сумма"] = cell, ["Итого"] = total }],
        1, new Dictionary<string, TableTotal>(), Keys: ["1"],
        Breakdown: new(rows, narrowed, "оговорка модуля"));

    /// <summary>Служба закрытого не считает — клеток с суммами в строке нет, сверять не с чем.</summary>
    private static ModuleTablePage WithoutCells(ModuleTablePage page) =>
        page with { Rows = [new Dictionary<string, object?> { ["Номер"] = "1" }] };

    private static HashSet<string> Open(params string[] money) => [.. money.Append("Номер")];

    private static List<TableColumnDto> Columns(string? unavailable = null, string? reason = null) =>
    [
        new("Номер", "Номер", "text", [], true),
        new("Сумма", "Сумма", "number", [], true, unavailable, reason),
        new("Итого", "Итого", "number", [], true, unavailable, reason),
    ];

    private sealed class ProbeRows : IModuleTableRows
    {
        public Task<ModuleTablePage> ReadAsync(ModuleTableQuery query, CancellationToken ct) =>
            Task.FromResult(new ModuleTablePage([], 0, new Dictionary<string, TableTotal>()));
    }
}
