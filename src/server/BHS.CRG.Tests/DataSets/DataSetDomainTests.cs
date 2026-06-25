using BHS.CRG.Domain.DataSets;

namespace BHS.CRG.Tests.DataSets;

public class DataSetDomainTests
{
    [Fact]
    public void Template_Create_TrimsNameAndSetsFields()
    {
        var docTypeId = Guid.NewGuid();
        var t = DataSetBindingTemplate.Create(docTypeId, "  Список  ", "Материалы", "{}", null, null, 3);

        Assert.Equal(docTypeId, t.DocumentTypeId);
        Assert.Equal("Список", t.Name);
        Assert.Equal("Материалы", t.TargetFieldKey);
        Assert.Equal("{}", t.ColumnMappings);
        Assert.Null(t.RowFilter);
        Assert.Null(t.ComputedColumns);
        Assert.Equal(3, t.SortOrder);
    }

    [Fact]
    public void Template_Update_ReplacesValues()
    {
        var t = DataSetBindingTemplate.Create(Guid.NewGuid(), "A", null, "{}", null, null);
        t.Update("  B  ", "Поле", """{"k":"v"}""", """{"logic":"and"}""", "[]", 5);

        Assert.Equal("B", t.Name);
        Assert.Equal("Поле", t.TargetFieldKey);
        Assert.Equal("""{"k":"v"}""", t.ColumnMappings);
        Assert.Equal("""{"logic":"and"}""", t.RowFilter);
        Assert.Equal("[]", t.ComputedColumns);
        Assert.Equal(5, t.SortOrder);
    }

    [Fact]
    public void Binding_Create_DefaultsFiltersToNull()
    {
        var b = DataSetBinding.Create(Guid.NewGuid(), Guid.NewGuid(), null, "{}");
        Assert.Null(b.TargetFieldKey);
        Assert.Equal("{}", b.Mapping);
        Assert.Null(b.RowFilter);
        Assert.Null(b.ComputedColumns);
    }

    [Fact]
    public void Binding_Update_SetsFilterAndComputed()
    {
        var b = DataSetBinding.Create(Guid.NewGuid(), Guid.NewGuid(), null, "{}");
        b.Update("Таблица", """{"a":"b"}""", """{"logic":"or"}""", "[{}]");

        Assert.Equal("Таблица", b.TargetFieldKey);
        Assert.Equal("""{"a":"b"}""", b.Mapping);
        Assert.Equal("""{"logic":"or"}""", b.RowFilter);
        Assert.Equal("[{}]", b.ComputedColumns);
    }
}
