using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Tables;
using BHS.CRG.Domain.Common;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Modules.Tables;

namespace BHS.CRG.Tests.DataSets;

/// <summary>
/// Четыре узла дерева условий и сравнение по виду колонки (задача G1c, issue #1090, ТЗ CORE-33).
/// Согласие исполнителя в памяти с запросом к базе сторожит <c>ModuleTableFilterPairTests</c>; здесь —
/// правила сами по себе, без базы.
/// </summary>
public class TableConditionsTests
{
    private static List<IReadOnlyDictionary<string, string?>> Rows(string column, params string?[] cells) =>
        [.. cells.Select(c => (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?> { [column] = c })];

    private static string One(string column, string op, string value) =>
        $$"""{"type":"condition","column":"{{column}}","op":"{{op}}","value":"{{value}}"}""";

    private static string Many(string column, string op, params string[] values) =>
        $$"""{"type":"condition","column":"{{column}}","op":"{{op}}","values":[{{string.Join(",", values.Select(v => $"\"{v}\""))}}]}""";

    private static DataSetColumnTypes Typed(string column, string kind) =>
        new(new Dictionary<string, string> { [column] = kind }, new Dictionary<string, string>());

    // ── Узлы у файловых наборов: та же догадка, что у остальных сравнений ──────

    [Fact]
    public void Перечень_период_и_неопределённость_работают_у_файлового_набора()
    {
        var rows = Rows("К", "10", "5", "кабель", "", null);

        Assert.Equal(2, DataSetRowFilterExecutor.Apply(Many("К", "in", "10", "КАБЕЛЬ"), rows).Count);
        Assert.Equal(3, DataSetRowFilterExecutor.Apply(Many("К", "not_in", "10", "КАБЕЛЬ"), rows).Count);
        Assert.Equal(2, DataSetRowFilterExecutor.Apply(Many("К", "between", "5", "10"), rows).Count);
        Assert.Equal(2, DataSetRowFilterExecutor.Apply("""{"type":"condition","column":"К","op":"is_null"}""", rows).Count);
        Assert.Equal(3, DataSetRowFilterExecutor.Apply("""{"type":"condition","column":"К","op":"is_not_null"}""", rows).Count);
    }

    [Theory]
    [InlineData("""{"type":"condition","column":"К","op":"between","values":["1"]}""", "две границы")]
    [InlineData("""{"type":"condition","column":"К","op":"between","value":"1"}""", "две границы")]
    [InlineData("""{"type":"condition","column":"К","op":"in"}""", "список значений")]
    [InlineData("""{"type":"condition","column":"К","op":"in","values":[]}""", "список значений")]
    public void Узел_без_нужных_значений_отказывает(string filter, string reason)
    {
        var refusal = Assert.Throws<ConflictException>(() => DataSetRowFilterExecutor.Apply(filter, Rows("К", "1")));
        Assert.Contains(reason, refusal.Message);
    }

    /// <summary>У файлового набора поведение прежнее: на догадке стоят сохранённые отборы.</summary>
    [Fact]
    public void Без_видов_колонок_сравнение_идёт_по_догадке_как_прежде()
    {
        var rows = Rows("Сумма", "110.00", "", "12 шт");

        // Строки разные — «равно» не находит; пустая строка и «12 шт» «меньше» 500 как строки.
        Assert.Empty(DataSetRowFilterExecutor.Apply(One("Сумма", "eq", "110"), rows));
        Assert.Equal(3, DataSetRowFilterExecutor.Apply(One("Сумма", "lt", "500"), rows).Count);
    }

    // ── Сравнение по виду ─────────────────────────────────────────────────────

    [Fact]
    public void С_видом_число_сравнивается_числом_а_клетка_с_текстом_под_сравнение_не_попадает()
    {
        var rows = Rows("Сумма", "110.00", "", "12 шт", null, "99.5");
        var types = Typed("Сумма", TableOperators.Number);
        int Count(string filter) => DataSetRowFilterExecutor.Apply(filter, rows, "т", types).Count;

        Assert.Equal(1, Count(One("Сумма", "eq", "110")));
        Assert.Equal(1, Count(One("Сумма", "lt", "100")));          // 99.5; пустые и «12 шт» — нет
        Assert.Equal(4, Count(One("Сумма", "neq", "110")));         // всё, что не 110, включая пустые и текст
        Assert.Equal(2, Count(Many("Сумма", "between", "99.5", "110")));
        Assert.Equal(2, Count("""{"type":"condition","column":"Сумма","op":"is_null"}"""));
        // «is_empty» у числа — прежнее имя того же вопроса: сохранённые отборы обязаны работать.
        Assert.Equal(2, Count("""{"type":"condition","column":"Сумма","op":"is_empty"}"""));
    }

    [Fact]
    public void С_видом_дата_сравниваются_первые_десять_знаков_а_не_дата_под_сравнение_не_попадает()
    {
        var rows = Rows("Срок", "2026-05-01", "2026-05-01T12:00:00", "скоро", null, "2026-06-01");
        var types = Typed("Срок", TableOperators.Date);
        int Count(string filter) => DataSetRowFilterExecutor.Apply(filter, rows, "т", types).Count;

        Assert.Equal(2, Count(One("Срок", "eq", "2026-05-01")));
        Assert.Equal(2, Count(One("Срок", "lt", "2026-06-01")));
        Assert.Equal(1, Count(One("Срок", "gt", "2026-05-01")));
    }

    [Theory]
    [InlineData("number", "contains", "1", "не применяется")]
    [InlineData("number", "gt", "много", "не число")]
    [InlineData("number", "gt", "1,5", "не число")]
    [InlineData("date", "lt", "01.05.2026", "не дата")]
    [InlineData("date", "lt", "2026-13-45", "не дата")]
    [InlineData("boolean", "eq", "да", "не «true»")]
    [InlineData("text", "between", "а", "не применяется")]
    public void Условие_негодное_для_вида_колонки_отказывает(string kind, string op, string value, string reason)
    {
        var filter = op == "between" ? Many("К", op, value, value) : One("К", op, value);

        var refusal = Assert.Throws<ConflictException>(() =>
            DataSetRowFilterExecutor.Apply(filter, Rows("К", "1"), "т", Typed("К", kind)));
        Assert.Contains(reason, refusal.Message);
    }

    /// <summary>Колонка без объявленного вида (вычисляемая) — по догадке и в типизированном наборе.</summary>
    [Fact]
    public void Колонка_без_вида_в_типизированном_наборе_сравнивается_по_догадке()
    {
        var rows = Rows("Расчёт", "110.00", "");
        var types = Typed("Другая", TableOperators.Number);

        Assert.Empty(DataSetRowFilterExecutor.Apply(One("Расчёт", "eq", "110"), rows, "т", types));
    }

    // ── Договор двух исполнителей ─────────────────────────────────────────────

    /// <summary>
    /// Запись числа и даты — одна у обоих исполнителей. Проект контрактов наших проектов не видит,
    /// поэтому у запроса к базе своя копия; разойдись они — клетку «1e3» один исполнитель счёл бы
    /// числом, другой текстом.
    /// </summary>
    [Fact]
    public void Запись_числа_и_даты_у_исполнителей_одна()
    {
        Assert.Equal(TableConditions.NumberPattern, TableSqlRules.NumberPattern);
        Assert.Equal(TableConditions.DatePattern, TableSqlRules.DatePattern);
    }

    private sealed class Probe
    {
        public string? Number { get; set; }
    }

    /// <summary>
    /// Запросу описаны ВСЕ объявленные колонки. Забытая ушла бы в поля схемы, отбор по ней искал бы
    /// ключ в данных записи и отвечал «ничего не найдено» — отказом, переодетым в результат.
    /// </summary>
    [Fact]
    public void Запрос_без_описания_объявленной_колонки_отказывает_и_называет_её()
    {
        var table = new ModuleTable(
            "probe", "Проба", "запись", "probe", ModuleTableIsolation.None, "Отдаёт всё",
            [new("Номер", "Номер", ModuleTableColumnKind.Text), new("Сумма", "Сумма", ModuleTableColumnKind.Number)],
            typeof(object));
        var sql = new TableSql<Probe>(table).Text("Номер", p => p.Number).Fields(_ => p => p.Number);
        var condition = new TableFilterCondition("Сумма", ModuleTableColumnKind.Number, "gt", ["1"], _ => true);

        var error = Assert.Throws<InvalidOperationException>(() =>
            sql.Where(Array.Empty<Probe>().AsQueryable(), condition));
        Assert.Contains("«Сумма»", error.Message);

        // И колонка, описанная не тем видом, — тоже: число, описанное текстом, сравнивалось бы строками.
        var wrong = new TableSql<Probe>(table).Text("Номер", p => p.Number).Text("Сумма", p => p.Number);
        Assert.Contains("«Сумма»", Assert.Throws<InvalidOperationException>(() =>
            wrong.Where(Array.Empty<Probe>().AsQueryable(), condition)).Message);
    }
}
