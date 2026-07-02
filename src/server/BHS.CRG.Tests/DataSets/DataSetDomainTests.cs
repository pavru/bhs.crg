using BHS.CRG.Domain.DataSets;

namespace BHS.CRG.Tests.DataSets;

public class DataSetDomainTests
{
    [Fact]
    public void BindingTemplate_Create_TrimsNameAndSetsFields()
    {
        var docTypeId = Guid.NewGuid();
        var t = DataSetBindingTemplate.Create(docTypeId, "  Список  ", "Материалы", "{}", 3);

        Assert.Equal(docTypeId, t.DocumentTypeId);
        Assert.Equal("Список", t.Name);
        Assert.Equal("Материалы", t.TargetFieldKey);
        Assert.Equal("{}", t.ColumnMappings);
        Assert.Equal(3, t.SortOrder);
    }

    [Fact]
    public void BindingTemplate_Update_ReplacesValues()
    {
        var t = DataSetBindingTemplate.Create(Guid.NewGuid(), "A", null, "{}");
        t.Update("  B  ", "Поле", """{"k":"v"}""", 5);

        Assert.Equal("B", t.Name);
        Assert.Equal("Поле", t.TargetFieldKey);
        Assert.Equal("""{"k":"v"}""", t.ColumnMappings);
        Assert.Equal(5, t.SortOrder);
    }

    [Fact]
    public void Binding_Create_DefaultsMapping()
    {
        var b = DataSetBinding.Create(Guid.NewGuid(), Guid.NewGuid(), null, "{}");
        Assert.Null(b.TargetFieldKey);
        Assert.Equal("{}", b.Mapping);
    }

    [Fact]
    public void Binding_Update_SetsTargetAndMapping()
    {
        var b = DataSetBinding.Create(Guid.NewGuid(), Guid.NewGuid(), null, "{}");
        b.Update("Таблица", """{"a":"b"}""");

        Assert.Equal("Таблица", b.TargetFieldKey);
        Assert.Equal("""{"a":"b"}""", b.Mapping);
    }

    [Fact]
    public void ProcessingTemplate_Create_TrimsNameAndSetsFields()
    {
        var t = DataSetProcessingTemplate.Create("  Стандартный  ", """{"logic":"and"}""", "[]", """[{"column":"A"}]""");

        Assert.Equal("Стандартный", t.Name);
        Assert.Equal("""{"logic":"and"}""", t.RowFilter);
        Assert.Equal("[]", t.ComputedColumns);
        Assert.Equal("""[{"column":"A"}]""", t.SortSpec);
    }

    [Fact]
    public void ProcessingTemplate_Update_ReplacesValues()
    {
        var t = DataSetProcessingTemplate.Create("A", null, null, null);
        t.Update("  B  ", """{"logic":"or"}""", "[{}]", "[]");

        Assert.Equal("B", t.Name);
        Assert.Equal("""{"logic":"or"}""", t.RowFilter);
        Assert.Equal("[{}]", t.ComputedColumns);
        Assert.Equal("[]", t.SortSpec);
    }

    [Fact]
    public void Source_SetProcessing_ReplacesOwnValues()
    {
        var file = DataSetFile.Create("f", DataSetFormat.Xml, "blob", Domain.Catalog.CatalogScope.System, null);
        var source = file.AddSource("s", "/Root/Item", "[]", 0);

        source.SetProcessing("""{"logic":"and"}""", "[]", "[]");
        Assert.Equal("""{"logic":"and"}""", source.RowFilter);
        Assert.Equal("[]", source.ComputedColumns);
        Assert.Equal("[]", source.SortSpec);

        // Применение шаблона копирует значения единожды — источник дальше независим от шаблона.
        source.SetProcessing("""{"logic":"or"}""", "[{}]", """[{"column":"A"}]""");
        Assert.Equal("""{"logic":"or"}""", source.RowFilter);
        Assert.Equal("[{}]", source.ComputedColumns);
        Assert.Equal("""[{"column":"A"}]""", source.SortSpec);
    }
}
