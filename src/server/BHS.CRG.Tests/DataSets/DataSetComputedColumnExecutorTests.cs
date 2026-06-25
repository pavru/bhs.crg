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
        var defs = """[{"alias":"ФИО","expr":"{{ Фамилия }} {{ Имя }}"}]""";
        var result = DataSetComputedColumnExecutor.Apply(defs, rows);
        Assert.Equal("Иванов Иван", result[0]["ФИО"]);
    }

    [Fact]
    public void OriginalColumnsPreserved()
    {
        var rows = Rows([("A", "1"), ("B", "2")]);
        var result = DataSetComputedColumnExecutor.Apply("""[{"alias":"C","expr":"{{ A }}"}]""", rows);
        Assert.Equal("1", result[0]["A"]);
        Assert.Equal("2", result[0]["B"]);
        Assert.Equal("1", result[0]["C"]);
    }

    [Fact]
    public void ColumnNameWithSpace_AccessibleViaUnderscore()
    {
        // Column "Полное Имя" → identifier "Полное_Имя" inside the expression.
        var rows = Rows([("Полное Имя", "Пётр")]);
        var defs = """[{"alias":"X","expr":"{{ Полное_Имя }}"}]""";
        var result = DataSetComputedColumnExecutor.Apply(defs, rows);
        Assert.Equal("Пётр", result[0]["X"]);
    }

    [Fact]
    public void MultipleDefinitions_AllApplied()
    {
        var rows = Rows([("A", "1"), ("B", "2")]);
        var defs = """[{"alias":"X","expr":"{{ A }}"},{"alias":"Y","expr":"{{ B }}"}]""";
        var result = DataSetComputedColumnExecutor.Apply(defs, rows);
        Assert.Equal("1", result[0]["X"]);
        Assert.Equal("2", result[0]["Y"]);
    }

    [Fact]
    public void AppliedToEveryRow()
    {
        var rows = Rows([("A", "1")], [("A", "2")]);
        var result = DataSetComputedColumnExecutor.Apply("""[{"alias":"B","expr":"{{ A }}!"}]""", rows);
        Assert.Equal("1!", result[0]["B"]);
        Assert.Equal("2!", result[1]["B"]);
    }

    [Fact]
    public void EmptyAliasOrExpr_Skipped()
    {
        var rows = Rows([("A", "1")]);
        var defs = """[{"alias":"","expr":"{{ A }}"},{"alias":"B","expr":""}]""";
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
        // Computed runs before filter — verify a computed column produces filterable values.
        var rows = Rows([("A", "5"), ("B", "5")], [("A", "1"), ("B", "9")]);
        var withSum = DataSetComputedColumnExecutor.Apply(
            """[{"alias":"Eq","expr":"{{ A == B }}"}]""", rows);
        Assert.Equal("true", withSum[0]["Eq"]);
        Assert.Equal("false", withSum[1]["Eq"]);
    }
}
