using BHS.CRG.Infrastructure.DataSets;

namespace BHS.CRG.Tests.DataSets;

public class DataSetComputedColumnExecutorTests
{
    private static List<IReadOnlyDictionary<string, string?>> Rows(params (string col, string? val)[][] rows) =>
        rows.Select(r => (IReadOnlyDictionary<string, string?>)r.ToDictionary(c => c.col, c => c.val)).ToList();

    [Fact]
    public void NullOrEmpty_ReturnsUnchanged()
    {
        var rows = Rows([("A", "1")]);
        Assert.Same(rows, DataSetComputedColumnExecutor.Apply(null, rows));
        Assert.Same(rows, DataSetComputedColumnExecutor.Apply("", rows));
    }

    [Fact]
    public void AddsComputedColumn_FromExpression()
    {
        var rows = Rows([("Фамилия", "Иванов"), ("Имя", "Иван")]);
        var defs = """[{"alias":"ФИО","expr":"Фамилия + ' ' + Имя"}]""";
        var result = DataSetComputedColumnExecutor.Apply(defs, rows);
        Assert.Equal("Иванов Иван", result[0]["ФИО"]);
    }

    [Fact]
    public void OriginalColumnsPreserved()
    {
        var rows = Rows([("A", "1"), ("B", "2")]);
        var result = DataSetComputedColumnExecutor.Apply("""[{"alias":"C","expr":"A"}]""", rows);
        Assert.Equal("1", result[0]["A"]);
        Assert.Equal("2", result[0]["B"]);
        Assert.Equal("1", result[0]["C"]);
    }

    [Fact]
    public void ColumnNameWithSpace_AccessibleViaUnderscore()
    {
        // "Полное Имя" → sanitized to "Полное_Имя" as a JS identifier
        var rows = Rows([("Полное Имя", "Пётр")]);
        var defs = """[{"alias":"X","expr":"Полное_Имя"}]""";
        var result = DataSetComputedColumnExecutor.Apply(defs, rows);
        Assert.Equal("Пётр", result[0]["X"]);
    }

    [Fact]
    public void MultipleDefinitions_AllApplied()
    {
        var rows = Rows([("A", "1"), ("B", "2")]);
        var defs = """[{"alias":"X","expr":"A"},{"alias":"Y","expr":"B"}]""";
        var result = DataSetComputedColumnExecutor.Apply(defs, rows);
        Assert.Equal("1", result[0]["X"]);
        Assert.Equal("2", result[0]["Y"]);
    }

    [Fact]
    public void AppliedToEveryRow()
    {
        var rows = Rows([("A", "1")], [("A", "2")]);
        var result = DataSetComputedColumnExecutor.Apply("""[{"alias":"B","expr":"A + '!'"}]""", rows);
        Assert.Equal("1!", result[0]["B"]);
        Assert.Equal("2!", result[1]["B"]);
    }

    [Fact]
    public void EmptyAliasOrExpr_Skipped()
    {
        var rows = Rows([("A", "1")]);
        var defs = """[{"alias":"","expr":"A"},{"alias":"B","expr":""}]""";
        var result = DataSetComputedColumnExecutor.Apply(defs, rows);
        Assert.False(result[0].ContainsKey("B"));
        Assert.False(result[0].ContainsKey(""));
    }

    [Fact]
    public void MalformedJson_ReturnsUnchanged()
    {
        var rows = Rows([("A", "1")]);
        var result = DataSetComputedColumnExecutor.Apply("{ broken", rows);
        Assert.Single(result);
        Assert.False(result[0].ContainsKey("B"));
    }

    [Fact]
    public void ComputedColumnCanBeFilteredAfterwards()
    {
        // JS strict equality of string values; String() converts boolean to "true"/"false"
        var rows = Rows([("A", "5"), ("B", "5")], [("A", "1"), ("B", "9")]);
        var withEq = DataSetComputedColumnExecutor.Apply(
            """[{"alias":"Eq","expr":"String(A === B)"}]""", rows);
        Assert.Equal("true", withEq[0]["Eq"]);
        Assert.Equal("false", withEq[1]["Eq"]);
    }

    [Fact]
    public void ArithmeticExpression_WorksWithParseFloat()
    {
        var rows = Rows([("Цена", "100"), ("Кол", "3")]);
        var defs = """[{"alias":"Итого","expr":"String(parseFloat(Цена) * parseFloat(Кол))"}]""";
        var result = DataSetComputedColumnExecutor.Apply(defs, rows);
        Assert.Equal("300", result[0]["Итого"]);
    }

    [Fact]
    public void InvalidExpression_SetsNullAndContinues()
    {
        var rows = Rows([("A", "1")]);
        var defs = """[{"alias":"B","expr":"!!!((("}]""";
        var result = DataSetComputedColumnExecutor.Apply(defs, rows);
        // Bad expression → column gets null, no exception thrown
        Assert.True(result[0].ContainsKey("B"));
        Assert.Null(result[0]["B"]);
    }

    // ── Доступ к колонкам с заголовками-неидентификаторами (issue #539) ──────────

    /// <summary>
    /// Числовой заголовок — обычное дело для таблиц из распознавания PDF. Естественное выражение
    /// «1» при этом НЕ ошибка: движок честно вернёт число, и раньше это был единственный исход.
    /// </summary>
    [Fact]
    public void NumericHeader_ReadableByNameAndPosition()
    {
        var rows = Rows([("1", "Кабель"), ("2", "12 м")]);

        Assert.Equal("Кабель", DataSetComputedColumnExecutor
            .Apply("""[{"alias":"X","expr":"get('1')"}]""", rows)[0]["X"]);
        Assert.Equal("Кабель", DataSetComputedColumnExecutor
            .Apply("""[{"alias":"X","expr":"cols[1]"}]""", rows)[0]["X"]);
        Assert.Equal("12 м", DataSetComputedColumnExecutor
            .Apply("""[{"alias":"X","expr":"cols[2]"}]""", rows)[0]["X"]);
        // совместимость: голое «1» по-прежнему число, а не ячейка
        Assert.Equal("1", DataSetComputedColumnExecutor
            .Apply("""[{"alias":"X","expr":"1"}]""", rows)[0]["X"]);
    }

    /// <summary>Счёт с ЕДИНИЦЫ — решение пользователя; нулевой элемент пуст.</summary>
    [Fact]
    public void PositionalAccess_IsOneBased()
    {
        var rows = Rows([("A", "первая"), ("B", "вторая")]);
        Assert.Equal("первая", DataSetComputedColumnExecutor
            .Apply("""[{"alias":"X","expr":"cols[1]"}]""", rows)[0]["X"]);
        // нулевой элемент пуст, и из-за него длина на единицу больше числа колонок — это
        // закреплено тестом, потому что так написано и в подсказке пользователю
        Assert.Equal("true", DataSetComputedColumnExecutor
            .Apply("""[{"alias":"X","expr":"String(cols[0] == null)"}]""", rows)[0]["X"]);
        Assert.Equal("3", DataSetComputedColumnExecutor
            .Apply("""[{"alias":"X","expr":"String(cols.length)"}]""", rows)[0]["X"]);
    }

    /// <summary>Дефис движок читает как вычитание — по имени колонка недостижима, через get достижима.</summary>
    [Fact]
    public void HeaderWithDash_ReadableByGet()
    {
        var rows = Rows([("Кол-во", "7")]);
        Assert.Equal("7", DataSetComputedColumnExecutor
            .Apply("""[{"alias":"X","expr":"get('Кол-во')"}]""", rows)[0]["X"]);
    }

    /// <summary>
    /// Столкновение просанированных имён: «№» и «%» оба дают «_», и переменная остаётся одна.
    /// Само затирание не трогаем (менять победителя — молча менять смысл сохранённых выражений),
    /// но обе колонки обязаны быть достижимы по точному заголовку.
    /// </summary>
    [Fact]
    public void CollidingHeaders_BothReachableByGet()
    {
        var rows = Rows([("№", "первая"), ("%", "вторая")]);
        var result = DataSetComputedColumnExecutor
            .Apply("""[{"alias":"X","expr":"get('№') + '|' + get('%') + '|' + _"}]""", rows);
        Assert.Equal("первая|вторая|вторая", result[0]["X"]);
    }

    /// <summary>
    /// Рваные строки: во второй строке нет колонки «B», и если бы позиции считались по ключам
    /// САМОЙ строки, cols[2] означал бы в разных строках разные колонки.
    /// </summary>
    [Fact]
    public void RaggedRows_KeepColumnPositions()
    {
        var rows = Rows(
            [("A", "a1"), ("B", "b1"), ("C", "c1")],
            [("A", "a2"), ("C", "c2")]);
        var result = DataSetComputedColumnExecutor.Apply("""[{"alias":"X","expr":"cols[3]"}]""", rows);
        Assert.Equal("c1", result[0]["X"]);
        Assert.Equal("c2", result[1]["X"]);
    }

    /// <summary>Отсутствующая колонка отличима от пустого значения: null против пустой строки.</summary>
    [Fact]
    public void Get_DistinguishesMissingColumnFromEmptyValue()
    {
        var rows = Rows([("A", null)]);
        var result = DataSetComputedColumnExecutor.Apply(
            """[{"alias":"X","expr":"String(get('A') === '') + '|' + String(get('Б') === null)"}]""", rows);
        Assert.Equal("true|true", result[0]["X"]);
    }

    /// <summary>Предыдущая вычисляемая колонка видна следующей и по имени, и по позиции.</summary>
    [Fact]
    public void EarlierComputedColumn_VisibleToNextOne()
    {
        var rows = Rows([("A", "x")]);
        var defs = """[{"alias":"B","expr":"A + '!'"},{"alias":"C","expr":"get('B') + cols[2]"}]""";
        var result = DataSetComputedColumnExecutor.Apply(defs, rows);
        Assert.Equal("x!x!", result[0]["C"]);
    }

    /// <summary>cols — настоящий JS-массив, а не обёртка: срез и join работают.</summary>
    [Fact]
    public void Cols_IsRealJsArray()
    {
        var rows = Rows([("A", "1"), ("B", "2"), ("C", "3")]);
        var result = DataSetComputedColumnExecutor.Apply(
            """[{"alias":"X","expr":"cols.slice(1).join('-')"}]""", rows);
        Assert.Equal("1-2-3", result[0]["X"]);
    }
}
