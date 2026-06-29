using BHS.CRG.Infrastructure.DataSets;

namespace BHS.CRG.Tests.DataSets;

public class DataSetMappingValueTests
{
    [Fact]
    public void PlainColumn_IsNotRef()
    {
        Assert.False(DataSetMappingValue.IsRef("Наименование"));
        Assert.Null(DataSetMappingValue.ParseRef("Наименование"));
    }

    [Fact]
    public void RefValue_ParsesColumnMatchAndType()
    {
        var typeId = Guid.NewGuid();
        var value = $$"""@@ref:{"column":"ИНН","match":"ИНН","typeId":"{{typeId}}"}""";

        Assert.True(DataSetMappingValue.IsRef(value));
        var parsed = DataSetMappingValue.ParseRef(value);
        Assert.NotNull(parsed);
        Assert.Equal("ИНН", parsed!.Column);
        Assert.Equal("ИНН", parsed.Match);
        Assert.Equal(typeId, parsed.TypeId);
    }

    [Fact]
    public void RefValue_EmptyMatch_MeansByDisplayName()
    {
        var typeId = Guid.NewGuid();
        var value = $$"""@@ref:{"column":"Организация","match":"","typeId":"{{typeId}}"}""";
        var parsed = DataSetMappingValue.ParseRef(value);
        Assert.NotNull(parsed);
        Assert.Equal("", parsed!.Match);
    }

    [Theory]
    [InlineData("@@ref:not-json")]
    [InlineData("@@ref:{\"column\":\"X\"}")]                       // нет typeId
    [InlineData("@@ref:{\"typeId\":\"00000000-0000-0000-0000-000000000000\",\"column\":\"X\"}")] // пустой Guid
    [InlineData("@@ref:{\"typeId\":\"11111111-1111-1111-1111-111111111111\",\"column\":\"\"}")]  // нет column
    public void Malformed_ReturnsNull(string value)
    {
        Assert.Null(DataSetMappingValue.ParseRef(value));
    }
}
