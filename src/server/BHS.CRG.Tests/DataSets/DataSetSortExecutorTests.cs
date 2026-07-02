using BHS.CRG.Infrastructure.DataSets;

namespace BHS.CRG.Tests.DataSets;

public class DataSetSortExecutorTests
{
    private static IReadOnlyDictionary<string, string?> Row(params (string, string?)[] kv)
        => kv.ToDictionary(p => p.Item1, p => p.Item2);

    [Fact]
    public void NoSpec_ReturnsRowsUnchanged()
    {
        var rows = new List<IReadOnlyDictionary<string, string?>> { Row(("A", "2")), Row(("A", "1")) };
        var result = DataSetSortExecutor.Apply(null, rows);
        Assert.Same(rows, result);
    }

    [Fact]
    public void SortsNumericAscending()
    {
        var rows = new List<IReadOnlyDictionary<string, string?>> { Row(("A", "10")), Row(("A", "2")), Row(("A", "1")) };
        var result = DataSetSortExecutor.Apply("""[{"column":"A","direction":"asc"}]""", rows);
        Assert.Equal(["1", "2", "10"], result.Select(r => r["A"]));
    }

    [Fact]
    public void SortsNumericDescending()
    {
        var rows = new List<IReadOnlyDictionary<string, string?>> { Row(("A", "1")), Row(("A", "10")), Row(("A", "2")) };
        var result = DataSetSortExecutor.Apply("""[{"column":"A","direction":"desc"}]""", rows);
        Assert.Equal(["10", "2", "1"], result.Select(r => r["A"]));
    }

    [Fact]
    public void NullsAlwaysLast_RegardlessOfDirection()
    {
        var rows = new List<IReadOnlyDictionary<string, string?>>
        {
            Row(("A", "5")), Row(("A", null)), Row(("A", "1")),
        };

        var asc = DataSetSortExecutor.Apply("""[{"column":"A","direction":"asc"}]""", rows);
        Assert.Equal(["1", "5", null], asc.Select(r => r["A"]));

        var desc = DataSetSortExecutor.Apply("""[{"column":"A","direction":"desc"}]""", rows);
        Assert.Equal(["5", "1", null], desc.Select(r => r["A"]));
    }

    [Fact]
    public void MultiLevelSort_ThenBy()
    {
        var rows = new List<IReadOnlyDictionary<string, string?>>
        {
            Row(("Group", "B"), ("Val", "2")),
            Row(("Group", "A"), ("Val", "2")),
            Row(("Group", "A"), ("Val", "1")),
        };
        var result = DataSetSortExecutor.Apply(
            """[{"column":"Group","direction":"asc"},{"column":"Val","direction":"asc"}]""", rows);

        Assert.Equal([("A", "1"), ("A", "2"), ("B", "2")],
            result.Select(r => (r["Group"], r["Val"])));
    }

    [Fact]
    public void SortsByComputedColumn()
    {
        // Проверяет, что сортировка видит любую колонку в строке — включая добавленную
        // на этапе Transformation (в реальном пайплайне она уже есть в словаре к этому моменту).
        var rows = new List<IReadOnlyDictionary<string, string?>>
        {
            Row(("Итого", "30")), Row(("Итого", "10")),
        };
        var result = DataSetSortExecutor.Apply("""[{"column":"Итого","direction":"asc"}]""", rows);
        Assert.Equal(["10", "30"], result.Select(r => r["Итого"]));
    }

    [Fact]
    public void StringFallback_WhenNotNumeric()
    {
        var rows = new List<IReadOnlyDictionary<string, string?>> { Row(("A", "banana")), Row(("A", "apple")) };
        var result = DataSetSortExecutor.Apply("""[{"column":"A","direction":"asc"}]""", rows);
        Assert.Equal(["apple", "banana"], result.Select(r => r["A"]));
    }

    [Fact]
    public void MalformedJson_ReturnsRowsUnchanged()
    {
        var rows = new List<IReadOnlyDictionary<string, string?>> { Row(("A", "1")) };
        var result = DataSetSortExecutor.Apply("not-json", rows);
        Assert.Same(rows, result);
    }
}
