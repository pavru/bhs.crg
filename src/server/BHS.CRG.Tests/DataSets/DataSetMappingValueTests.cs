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

    [Fact]
    public void RefValue_NameStrategy_ParsesColumn_NotIdentity()
    {
        var typeId = Guid.NewGuid();
        var value = $$"""@@ref:{"strategy":"Name","column":"Наименование","typeId":"{{typeId}}"}""";
        var parsed = DataSetMappingValue.ParseRef(value);
        Assert.NotNull(parsed);
        Assert.False(parsed!.IsIdentity);
        Assert.Equal("Наименование", parsed.Column);
        Assert.Equal("Name", parsed.Strategy);
    }

    [Fact]
    public void RefValue_IdentityStrategy_ParsesIdentityColumns()
    {
        var typeId = Guid.NewGuid();
        var value = $$"""@@ref:{"strategy":"Identity","identityColumns":{"ИНН":"КолИНН","КПП":"КолКПП"},"typeId":"{{typeId}}"}""";
        var parsed = DataSetMappingValue.ParseRef(value);
        Assert.NotNull(parsed);
        Assert.True(parsed!.IsIdentity);
        Assert.Null(parsed.Column);
        Assert.Equal(2, parsed.IdentityColumns!.Count);
        Assert.Equal("КолИНН", parsed.IdentityColumns["ИНН"]);
        Assert.Equal("КолКПП", parsed.IdentityColumns["КПП"]);
    }

    [Fact]
    public void RefValue_IdentityWithEmptyColumns_IsNotIdentity()
    {
        var typeId = Guid.NewGuid();
        // strategy=Identity, но identityColumns пуст → нечем резолвить как identity; и column есть → валиден как Name-подобный.
        var value = $$"""@@ref:{"strategy":"Identity","column":"Наименование","identityColumns":{},"typeId":"{{typeId}}"}""";
        var parsed = DataSetMappingValue.ParseRef(value);
        Assert.NotNull(parsed);
        Assert.False(parsed!.IsIdentity);
    }

    [Theory]
    [InlineData("@@ref:not-json")]
    [InlineData("@@ref:{\"column\":\"X\"}")]                       // нет typeId
    [InlineData("@@ref:{\"typeId\":\"00000000-0000-0000-0000-000000000000\",\"column\":\"X\"}")] // пустой Guid
    [InlineData("@@ref:{\"typeId\":\"11111111-1111-1111-1111-111111111111\",\"column\":\"\"}")]  // нет column и нет identityColumns
    public void Malformed_ReturnsNull(string value)
    {
        Assert.Null(DataSetMappingValue.ParseRef(value));
    }

    [Fact]
    public void RefValue_IdentityColumnsWithoutColumn_IsValid()
    {
        var typeId = Guid.NewGuid();
        var value = $$"""@@ref:{"strategy":"Identity","identityColumns":{"ИНН":"КолИНН"},"typeId":"{{typeId}}"}""";
        Assert.NotNull(DataSetMappingValue.ParseRef(value)); // нет column, но есть identityColumns → валиден
    }

    [Fact]
    public void PlainColumn_IsNotFile()
    {
        Assert.False(DataSetMappingValue.IsFile("Наименование"));
        Assert.Null(DataSetMappingValue.ParseFile("Наименование"));
    }

    [Fact]
    public void FileValue_ParsesColumnAndSizeColumn()
    {
        var value = """@@file:{"column":"ФайлПуть","sizeColumn":"РазмерБайт"}""";

        Assert.True(DataSetMappingValue.IsFile(value));
        Assert.False(DataSetMappingValue.IsRef(value));
        var parsed = DataSetMappingValue.ParseFile(value);
        Assert.NotNull(parsed);
        Assert.Equal("ФайлПуть", parsed!.Column);
        Assert.Equal("РазмерБайт", parsed.SizeColumn);
    }

    [Fact]
    public void FileValue_WithoutSizeColumn_ParsesWithNullSizeColumn()
    {
        var value = """@@file:{"column":"ФайлПуть"}""";
        var parsed = DataSetMappingValue.ParseFile(value);
        Assert.NotNull(parsed);
        Assert.Null(parsed!.SizeColumn);
    }

    [Theory]
    [InlineData("@@file:not-json")]
    [InlineData("@@file:{\"sizeColumn\":\"РазмерБайт\"}")] // нет column
    [InlineData("@@file:{\"column\":\"\"}")]               // пустой column
    public void FileValue_Malformed_ReturnsNull(string value)
    {
        Assert.Null(DataSetMappingValue.ParseFile(value));
    }

    [Fact]
    public void ResolveFileValue_BuildsAttachment_StrippingGuidPrefixAndDerivingMimeType()
    {
        var map = new DataSetFileMapping("ФайлПуть", "РазмерБайт");
        var row = new Dictionary<string, string?>
        {
            ["ФайлПуть"] = "bhs-crg/2026.07.02/1b1a5ae4-d46a-4c15-972f-9ad65c8920da_Floor Plan.pdf",
            ["РазмерБайт"] = "3051",
        };

        var result = DataSetMappingValue.ResolveFileValue(map, row);

        Assert.NotNull(result);
        Assert.Equal("file", result!["$type"]);
        Assert.Equal(row["ФайлПуть"], result["blobPath"]);
        Assert.Equal("Floor Plan.pdf", result["fileName"]);
        Assert.Equal("application/pdf", result["mimeType"]);
        Assert.Equal(3051L, result["size"]);
    }

    [Fact]
    public void ResolveFileValue_MissingSizeColumn_DefaultsToZero()
    {
        var map = new DataSetFileMapping("ФайлПуть", null);
        var row = new Dictionary<string, string?> { ["ФайлПуть"] = "abc_report.xls" };

        var result = DataSetMappingValue.ResolveFileValue(map, row);

        Assert.NotNull(result);
        Assert.Equal(0L, result!["size"]);
        Assert.Equal("application/vnd.ms-excel", result["mimeType"]);
    }

    [Fact]
    public void ResolveFileValue_EmptyOrMissingColumn_ReturnsNull()
    {
        var map = new DataSetFileMapping("ФайлПуть", null);
        Assert.Null(DataSetMappingValue.ResolveFileValue(map, new Dictionary<string, string?> { ["ФайлПуть"] = "" }));
        Assert.Null(DataSetMappingValue.ResolveFileValue(map, new Dictionary<string, string?>()));
    }
}
