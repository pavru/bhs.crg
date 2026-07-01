using System.Text;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.DataSets;

namespace BHS.CRG.Tests.DataSets;

public class XmlDataSetParserTests
{
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
    private readonly XmlDataSetParser _parser = new();

    private const string Sample = """
        <Root>
          <Item id="1"><Name>Кабель</Name><Qty>10</Qty></Item>
          <Item id="2"><Name>Лоток</Name><Qty>5</Qty></Item>
        </Root>
        """;

    [Fact]
    public void CanParse_OnlyXml()
    {
        Assert.True(_parser.CanParse(DataSetFormat.Xml));
        Assert.False(_parser.CanParse(DataSetFormat.Csv));
    }

    [Fact]
    public async Task DetectSources_AlwaysReturnsEmpty()
    {
        // Авто-детект по top-level элементам отключён — источники создаются только вручную.
        var sources = await _parser.DetectSourcesAsync(Utf8(Sample), default);
        Assert.Empty(sources);
    }

    [Fact]
    public async Task ParseAsync_ExplicitColumns_ChildAndAttribute()
    {
        var columnExpressions = """[{"name":"Артикул","expr":"@id"},{"name":"Наименование","expr":"Name"},{"name":"Количество","expr":"Qty"}]""";
        var result = await _parser.ParseAsync(Utf8(Sample), "/Root/Item", columnExpressions, default);

        Assert.Equal(["Артикул", "Наименование", "Количество"], result.Columns.Select(c => c.Name));
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("1", result.Rows[0]["Артикул"]);
        Assert.Equal("Кабель", result.Rows[0]["Наименование"]);
        Assert.Equal("10", result.Rows[0]["Количество"]);
        Assert.Equal("2", result.Rows[1]["Артикул"]);
        Assert.Equal("Лоток", result.Rows[1]["Наименование"]);
    }

    [Fact]
    public async Task ParseAsync_ScalarViaSingleMatchAndCondition()
    {
        // Row-selector с условием сужает до одного узла — тот же механизм, что и для табличных данных.
        var columnExpressions = """[{"name":"Наименование","expr":"Name"}]""";
        var result = await _parser.ParseAsync(Utf8(Sample), "/Root/Item[@id='2']", columnExpressions, default);

        var row = Assert.Single(result.Rows);
        Assert.Equal("Лоток", row["Наименование"]);
    }

    [Fact]
    public async Task ParseAsync_NestedRelativePath()
    {
        var xml = Utf8("""
            <Root>
              <Item><Info><Code>A1</Code></Info></Item>
            </Root>
            """);
        var columnExpressions = """[{"name":"Код","expr":"Info/Code"}]""";
        var result = await _parser.ParseAsync(xml, "/Root/Item", columnExpressions, default);

        Assert.Equal("A1", result.Rows[0]["Код"]);
    }

    [Fact]
    public async Task ParseAsync_NoMatches_ReturnsEmpty()
    {
        var result = await _parser.ParseAsync(Utf8(Sample), "/Root/Nothing", null, default);
        Assert.Empty(result.Columns);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task ParseAsync_WithoutColumnExpressions_FallsBackToAutoDiscovery()
    {
        var result = await _parser.ParseAsync(Utf8(Sample), "/Root/Item", null, default);

        Assert.Contains("@id", result.Columns.Select(c => c.Name));
        Assert.Contains("Name", result.Columns.Select(c => c.Name));
        Assert.Equal("1", result.Rows[0]["@id"]);
        Assert.Equal("Кабель", result.Rows[0]["Name"]);
    }

    [Fact]
    public async Task ParseAsync_MalformedColumnExpressionsJson_FallsBackToAuto()
    {
        var result = await _parser.ParseAsync(Utf8(Sample), "/Root/Item", "not-json", default);
        Assert.Contains("Name", result.Columns.Select(c => c.Name));
    }

    [Fact]
    public async Task ParseAsync_SampleValues_TakeFirstThreeRows()
    {
        var xml = Utf8("""
            <Root>
              <Item><V>1</V></Item><Item><V>2</V></Item>
              <Item><V>3</V></Item><Item><V>4</V></Item>
            </Root>
            """);
        var columnExpressions = """[{"name":"V","expr":"V"}]""";
        var result = await _parser.ParseAsync(xml, "/Root/Item", columnExpressions, default);

        var col = Assert.Single(result.Columns);
        Assert.Equal(["1", "2", "3"], col.SampleValues);
        Assert.Equal(4, result.Rows.Count);
    }
}
