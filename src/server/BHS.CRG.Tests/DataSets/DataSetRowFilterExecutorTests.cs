using BHS.CRG.Infrastructure.DataSets;

namespace BHS.CRG.Tests.DataSets;

public class DataSetRowFilterExecutorTests
{
    private static List<IReadOnlyDictionary<string, string?>> Rows(params (string col, string? val)[][] rows) =>
        rows.Select(r => (IReadOnlyDictionary<string, string?>)r.ToDictionary(c => c.col, c => c.val)).ToList();

    private static List<IReadOnlyDictionary<string, string?>> Sample() => Rows(
        [("Тип", "Кабель"), ("Кол", "10")],
        [("Тип", "Лоток"), ("Кол", "5")],
        [("Тип", "Кабель"), ("Кол", "0")]);

    private static string Condition(string column, string op, string? value = null) =>
        value is null
            ? $$"""{"type":"condition","column":"{{column}}","op":"{{op}}"}"""
            : $$"""{"type":"condition","column":"{{column}}","op":"{{op}}","value":"{{value}}"}""";

    private static string Group(string logic, params string[] children) =>
        $$"""{"type":"group","logic":"{{logic}}","children":[{{string.Join(",", children)}}]}""";

    [Fact]
    public void NullOrEmptyFilter_ReturnsAllRows()
    {
        var rows = Sample();
        Assert.Equal(3, DataSetRowFilterExecutor.Apply(null, rows).Count);
        Assert.Equal(3, DataSetRowFilterExecutor.Apply("", rows).Count);
        Assert.Equal(3, DataSetRowFilterExecutor.Apply("   ", rows).Count);
    }

    [Fact]
    public void EmptyGroup_ReturnsAllRows()
    {
        var result = DataSetRowFilterExecutor.Apply(Group("and"), Sample());
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void SingleEqCondition_Filters()
    {
        var json = Group("and", Condition("Тип", "eq", "Кабель"));
        var result = DataSetRowFilterExecutor.Apply(json, Sample());
        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.Equal("Кабель", r["Тип"]));
    }

    [Fact]
    public void AndLogic_RequiresAllConditions()
    {
        var json = Group("and", Condition("Тип", "eq", "Кабель"), Condition("Кол", "gt", "5"));
        var result = DataSetRowFilterExecutor.Apply(json, Sample());
        Assert.Single(result);
        Assert.Equal("10", result[0]["Кол"]);
    }

    [Fact]
    public void OrLogic_RequiresAnyCondition()
    {
        var json = Group("or", Condition("Тип", "eq", "Лоток"), Condition("Кол", "eq", "0"));
        var result = DataSetRowFilterExecutor.Apply(json, Sample());
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void NestedGroups_CombineLogic()
    {
        // Тип == Кабель AND (Кол == 10 OR Кол == 0)
        var inner = Group("or", Condition("Кол", "eq", "10"), Condition("Кол", "eq", "0"));
        var json = Group("and", Condition("Тип", "eq", "Кабель"), inner);
        var result = DataSetRowFilterExecutor.Apply(json, Sample());
        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.Equal("Кабель", r["Тип"]));
    }

    [Theory]
    [InlineData("eq", "Кабель", 2)]
    [InlineData("neq", "Кабель", 1)]
    [InlineData("contains", "абель", 2)]
    [InlineData("not_contains", "абель", 1)]
    [InlineData("starts_with", "Ка", 2)]
    [InlineData("ends_with", "ток", 1)]
    public void StringOperators(string op, string value, int expected)
    {
        var json = Group("and", Condition("Тип", op, value));
        Assert.Equal(expected, DataSetRowFilterExecutor.Apply(json, Sample()).Count);
    }

    [Theory]
    [InlineData("gt", "5", 1)]   // 10
    [InlineData("gte", "5", 2)]  // 10, 5
    [InlineData("lt", "5", 1)]   // 0
    [InlineData("lte", "5", 2)]  // 5, 0
    public void NumericComparison_UsesNumbers(string op, string value, int expected)
    {
        var json = Group("and", Condition("Кол", op, value));
        Assert.Equal(expected, DataSetRowFilterExecutor.Apply(json, Sample()).Count);
    }

    [Fact]
    public void EqIsCaseInsensitive()
    {
        var json = Group("and", Condition("Тип", "eq", "кабель"));
        Assert.Equal(2, DataSetRowFilterExecutor.Apply(json, Sample()).Count);
    }

    [Fact]
    public void IsEmptyAndIsNotEmpty()
    {
        var rows = Rows([("A", "x")], [("A", "")], [("A", null)]);
        Assert.Equal(2, DataSetRowFilterExecutor.Apply(Group("and", Condition("A", "is_empty")), rows).Count);
        Assert.Single(DataSetRowFilterExecutor.Apply(Group("and", Condition("A", "is_not_empty")), rows));
    }

    [Fact]
    public void MissingColumn_TreatedAsEmptyString()
    {
        var rows = Rows([("A", "x")]);
        // column "B" not present → empty → is_empty matches, eq "x" does not
        Assert.Single(DataSetRowFilterExecutor.Apply(Group("and", Condition("B", "is_empty")), rows));
        Assert.Empty(DataSetRowFilterExecutor.Apply(Group("and", Condition("B", "eq", "x")), rows));
    }

    [Fact]
    public void MalformedJson_ReturnsRowsUnchanged()
    {
        var rows = Sample();
        var result = DataSetRowFilterExecutor.Apply("{ not valid json", rows);
        Assert.Equal(3, result.Count);
    }
}
