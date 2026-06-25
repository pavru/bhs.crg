using BHS.CRG.Application.DataSets;

namespace BHS.CRG.Tests.DataSets;

public class DataSetAutoMapperTests
{
    [Fact]
    public void ExactKeyMatch_Wins()
    {
        var cols = new[] { "Наименование", "Кол" };
        var fields = new[] { new FieldInfo("Наименование", "Имя") };
        var map = DataSetAutoMapper.AutoMap(cols, fields);
        Assert.Equal("Наименование", map["Наименование"]);
    }

    [Fact]
    public void CaseInsensitiveKeyMatch()
    {
        var cols = new[] { "наименование" };
        var fields = new[] { new FieldInfo("Наименование", "Имя") };
        var map = DataSetAutoMapper.AutoMap(cols, fields);
        Assert.Equal("наименование", map["Наименование"]);
    }

    [Fact]
    public void TitleMatch_WhenKeyDoesNotMatch()
    {
        var cols = new[] { "Количество" };
        var fields = new[] { new FieldInfo("Кол", "Количество") };
        var map = DataSetAutoMapper.AutoMap(cols, fields);
        Assert.Equal("Количество", map["Кол"]);
    }

    [Fact]
    public void SubstringMatch_AsFallback()
    {
        var cols = new[] { "Количество, шт" };
        var fields = new[] { new FieldInfo("Количество", "Кол-во") };
        var map = DataSetAutoMapper.AutoMap(cols, fields);
        Assert.Equal("Количество, шт", map["Количество"]);
    }

    [Fact]
    public void NoMatch_FieldOmitted()
    {
        var cols = new[] { "ОтличноеИмя" };
        var fields = new[] { new FieldInfo("Цена", "Стоимость") };
        var map = DataSetAutoMapper.AutoMap(cols, fields);
        Assert.False(map.ContainsKey("Цена"));
    }

    [Fact]
    public void ExactMatchPreferredOverSubstring()
    {
        // Both "Кол" (exact) and "Количество" (substring of? no) present — exact wins.
        var cols = new[] { "Количество", "Кол" };
        var fields = new[] { new FieldInfo("Кол", "Количество") };
        var map = DataSetAutoMapper.AutoMap(cols, fields);
        Assert.Equal("Кол", map["Кол"]);
    }

    [Fact]
    public void MultipleFields_MappedIndependently()
    {
        var cols = new[] { "Имя", "Количество" };
        var fields = new[] { new FieldInfo("Имя", ""), new FieldInfo("Кол", "Количество") };
        var map = DataSetAutoMapper.AutoMap(cols, fields);
        Assert.Equal("Имя", map["Имя"]);
        Assert.Equal("Количество", map["Кол"]);
    }
}
