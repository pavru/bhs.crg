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
    public void Binding_ForInstance_DefaultsMapping()
    {
        var b = DataSetBinding.ForInstance(Guid.NewGuid(), Guid.NewGuid(), null, "{}");
        Assert.Null(b.TargetFieldKey);
        Assert.Equal("{}", b.Mapping);
        Assert.NotNull(b.InstanceId);
        Assert.Null(b.CommonDataEntryId);
    }

    [Fact]
    public void Binding_ForCommonDataEntry_SetsOwner()
    {
        var entryId = Guid.NewGuid();
        var b = DataSetBinding.ForCommonDataEntry(entryId, Guid.NewGuid(), "Чертежи", "{}");
        Assert.Equal(entryId, b.CommonDataEntryId);
        Assert.Null(b.InstanceId);
        Assert.Equal("Чертежи", b.TargetFieldKey);
    }

    [Fact]
    public void Binding_Update_SetsTargetAndMapping()
    {
        var b = DataSetBinding.ForInstance(Guid.NewGuid(), Guid.NewGuid(), null, "{}");
        b.Update("Таблица", """{"a":"b"}""");

        Assert.Equal("Таблица", b.TargetFieldKey);
        Assert.Equal("""{"a":"b"}""", b.Mapping);
    }

    [Fact]
    public void ProcessingTemplate_Create_TrimsNameAndSetsFields()
    {
        var t = DataSetProcessingTemplate.Create(
            "  Стандартный  ", "/Root/Item", """[{"name":"A","expr":"@a"}]""",
            """{"logic":"and"}""", "[]", """[{"column":"A"}]""");

        Assert.Equal("Стандартный", t.Name);
        Assert.Equal("/Root/Item", t.SheetOrPath);
        Assert.Equal("""[{"name":"A","expr":"@a"}]""", t.ColumnExpressions);
        Assert.Equal("""{"logic":"and"}""", t.RowFilter);
        Assert.Equal("[]", t.ComputedColumns);
        Assert.Equal("""[{"column":"A"}]""", t.SortSpec);
    }

    [Fact]
    public void ProcessingTemplate_Update_ReplacesValues()
    {
        var t = DataSetProcessingTemplate.Create("A", null, null, null, null, null);
        t.Update("  B  ", "/Root/Item", """[{"name":"A","expr":"@a"}]""", """{"logic":"or"}""", "[{}]", "[]");

        Assert.Equal("B", t.Name);
        Assert.Equal("/Root/Item", t.SheetOrPath);
        Assert.Equal("""[{"name":"A","expr":"@a"}]""", t.ColumnExpressions);
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

    [Fact]
    public void Source_UpdateCache_WithCachedData_StoresRecognizedRows()
    {
        var file = DataSetFile.Create("f", DataSetFormat.Pdf, "blob", Domain.Catalog.CatalogScope.System, null);
        var source = file.AddSource("s", "titleblock-registry", "[]", 0);
        Assert.Null(source.CachedData);

        var data = """[{"Шифр":"АБВ.01"}]""";
        source.UpdateCache("""[{"name":"Шифр","sampleValues":["АБВ.01"]}]""", 1, data);

        Assert.Equal(1, source.CachedRowCount);
        Assert.Equal(data, source.CachedData);

        // Формат без cachedData (обычный перепарсинг) — не передаётся, остаётся null.
        source.UpdateCache("[]", 0);
        Assert.Null(source.CachedData);
    }

    [Fact]
    public void Source_SetTags_RoundTrips()
    {
        var file = DataSetFile.Create("f", DataSetFormat.Pdf, "blob", Domain.Catalog.CatalogScope.System, null);
        var source = file.AddSource("s", "titleblock-registry", "[]", 0);
        Assert.Null(source.Tags);

        source.SetTags("""["dataset.hasTitleBlock"]""");
        Assert.Equal("""["dataset.hasTitleBlock"]""", source.Tags);

        source.SetTags(null);
        Assert.Null(source.Tags);
    }
}
