using System.Text;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.DataSets;

namespace BHS.CRG.Tests.DataSets;

public class JsonDataSetParserTests
{
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
    private readonly JsonDataSetParser _parser = new();

    private const string Sample = """
        { "items": [
          { "id": 1, "name": "Кабель", "qty": 10 },
          { "id": 2, "name": "Лоток", "qty": 5 }
        ] }
        """;

    [Fact]
    public void CanParse_OnlyJson()
    {
        Assert.True(_parser.CanParse(DataSetFormat.Json));
        Assert.False(_parser.CanParse(DataSetFormat.Xml));
    }

    [Fact]
    public async Task DetectSources_TopLevelArrayProperty()
    {
        var sources = await _parser.DetectSourcesAsync(Utf8(Sample), default);
        var source = Assert.Single(sources);
        Assert.Equal("$.items", source.SheetOrPath);
        Assert.Equal(2, source.RowCount);
    }

    [Fact]
    public async Task ParseAsync_LegacyDollarPropPath_AutoUnwrapsArray()
    {
        // Совместимость со старым авто-детектом: "$.items" — одно совпадение-массив,
        // разворачивается в строки (без явного "[*]").
        var result = await _parser.ParseAsync(Utf8(Sample), "$.items", null, default);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("Кабель", result.Rows[0]["name"]);
    }

    [Fact]
    public async Task ParseAsync_ExplicitColumns_RelativeToRow()
    {
        var columnExpressions = """[{"name":"Артикул","expr":"id"},{"name":"Наименование","expr":"name"},{"name":"Количество","expr":"qty"}]""";
        var result = await _parser.ParseAsync(Utf8(Sample), "$.items[*]", columnExpressions, default);

        Assert.Equal(["Артикул", "Наименование", "Количество"], result.Columns.Select(c => c.Name));
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("1", result.Rows[0]["Артикул"]);
        Assert.Equal("Кабель", result.Rows[0]["Наименование"]);
        Assert.Equal("10", result.Rows[0]["Количество"]);
        Assert.Equal("2", result.Rows[1]["Артикул"]);
        Assert.Equal("Лоток", result.Rows[1]["Наименование"]);
    }

    [Fact]
    public async Task ParseAsync_ScalarViaFilterCondition()
    {
        // Row-selector с фильтром сужает до одного узла — тот же механизм, что и для табличных данных.
        var columnExpressions = """[{"name":"Наименование","expr":"name"}]""";
        var result = await _parser.ParseAsync(Utf8(Sample), "$.items[?(@.id==2)]", columnExpressions, default);

        var row = Assert.Single(result.Rows);
        Assert.Equal("Лоток", row["Наименование"]);
    }

    [Fact]
    public async Task ParseAsync_NestedRelativePath()
    {
        var json = Utf8("""{ "items": [ { "info": { "code": "A1" } } ] }""");
        var columnExpressions = """[{"name":"Код","expr":"info.code"}]""";
        var result = await _parser.ParseAsync(json, "$.items[*]", columnExpressions, default);

        Assert.Equal("A1", result.Rows[0]["Код"]);
    }

    [Fact]
    public async Task ParseAsync_RecursiveDescent_MatchesAcrossLevels()
    {
        var json = Utf8("""{ "a": { "price": 1 }, "b": { "c": { "price": 2 } } }""");
        var result = await _parser.ParseAsync(json, "$..price", null, default);

        Assert.Equal(2, result.Rows.Count);
        var col = Assert.Single(result.Columns);
        Assert.Equal("value", col.Name);
        Assert.Equal(["1", "2"], result.Rows.Select(r => r["value"]));
    }

    [Fact]
    public async Task ParseAsync_NoMatches_ReturnsEmpty()
    {
        var result = await _parser.ParseAsync(Utf8(Sample), "$.nothing", null, default);
        Assert.Empty(result.Columns);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task ParseAsync_WithoutColumnExpressions_FallsBackToAutoDiscovery()
    {
        var result = await _parser.ParseAsync(Utf8(Sample), "$.items[*]", null, default);

        Assert.Contains("id", result.Columns.Select(c => c.Name));
        Assert.Contains("name", result.Columns.Select(c => c.Name));
        Assert.Equal("1", result.Rows[0]["id"]);
        Assert.Equal("Кабель", result.Rows[0]["name"]);
    }

    [Fact]
    public async Task ParseAsync_MalformedColumnExpressionsJson_FallsBackToAuto()
    {
        var result = await _parser.ParseAsync(Utf8(Sample), "$.items[*]", "not-json", default);
        Assert.Contains("name", result.Columns.Select(c => c.Name));
    }

    [Fact]
    public async Task ParseAsync_SampleValues_TakeFirstThreeRows()
    {
        var json = Utf8("""{ "items": [ {"v":1}, {"v":2}, {"v":3}, {"v":4} ] }""");
        var columnExpressions = """[{"name":"V","expr":"v"}]""";
        var result = await _parser.ParseAsync(json, "$.items[*]", columnExpressions, default);

        var col = Assert.Single(result.Columns);
        Assert.Equal(["1", "2", "3"], col.SampleValues);
        Assert.Equal(4, result.Rows.Count);
    }
}
