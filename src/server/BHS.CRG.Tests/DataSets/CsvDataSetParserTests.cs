using System.Text;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.DataSets;

namespace BHS.CRG.Tests.DataSets;

public class CsvDataSetParserTests
{
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
    private readonly CsvDataSetParser _parser = new();

    [Fact]
    public void CanParse_OnlyCsv()
    {
        Assert.True(_parser.CanParse(DataSetFormat.Csv));
        Assert.False(_parser.CanParse(DataSetFormat.Xlsx));
        Assert.False(_parser.CanParse(DataSetFormat.Json));
    }

    [Fact]
    public async Task ParsesCommaDelimited()
    {
        var bytes = Utf8("Имя,Количество\nКабель,10\nЛоток,5\n");
        var result = await _parser.ParseAsync(bytes, "default", default);

        Assert.Equal(["Имя", "Количество"], result.Columns.Select(c => c.Name));
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("Кабель", result.Rows[0]["Имя"]);
        Assert.Equal("10", result.Rows[0]["Количество"]);
        Assert.Equal("5", result.Rows[1]["Количество"]);
    }

    [Fact]
    public async Task DetectsTabDelimiter()
    {
        var bytes = Utf8("A\tB\n1\t2\n");
        var result = await _parser.ParseAsync(bytes, "default", default);
        Assert.Equal(["A", "B"], result.Columns.Select(c => c.Name));
        Assert.Equal("1", result.Rows[0]["A"]);
        Assert.Equal("2", result.Rows[0]["B"]);
    }

    [Fact]
    public async Task TrimsValues()
    {
        var bytes = Utf8("A,B\n  x  ,  y  \n");
        var result = await _parser.ParseAsync(bytes, "default", default);
        Assert.Equal("x", result.Rows[0]["A"]);
        Assert.Equal("y", result.Rows[0]["B"]);
    }

    [Fact]
    public async Task SampleValues_TakeFirstThreeRows()
    {
        var bytes = Utf8("A\n1\n2\n3\n4\n");
        var result = await _parser.ParseAsync(bytes, "default", default);
        var col = Assert.Single(result.Columns);
        Assert.Equal(["1", "2", "3"], col.SampleValues);
        Assert.Equal(4, result.Rows.Count);
    }

    [Fact]
    public async Task EmptyInput_ReturnsEmpty()
    {
        var result = await _parser.ParseAsync(Utf8(""), "default", default);
        Assert.Empty(result.Columns);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task DetectSources_ReturnsSingleDefaultSource()
    {
        var bytes = Utf8("A,B\n1,2\n");
        var sources = await _parser.DetectSourcesAsync(bytes, default);
        var src = Assert.Single(sources);
        Assert.Equal("default", src.SheetOrPath);
        Assert.Equal(1, src.RowCount);
        Assert.Equal(2, src.Columns.Count);
    }
}
