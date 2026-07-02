using BHS.CRG.Infrastructure.Recognition;

namespace BHS.CRG.Tests.Recognition;

public class InvoiceRecognitionSplitterTests
{
    [Fact]
    public void SplitHeader_ExtractsOnlyHeaderFields_IgnoresLineItemsKey()
    {
        var values = new Dictionary<string, string?>
        {
            ["НомерСчёта"] = "123",
            ["Поставщик"] = "ООО Ромашка",
            [InvoiceFields.LineItemsPath] = """[{"Наименование":"Кабель"}]""",
        };

        var header = InvoiceRecognitionSplitter.SplitHeader(values);

        Assert.Equal("123", header["НомерСчёта"]);
        Assert.Equal("ООО Ромашка", header["Поставщик"]);
        Assert.DoesNotContain(InvoiceFields.LineItemsPath, header.Keys);
        Assert.Equal(InvoiceFields.HeaderFields.Count, header.Count);
    }

    [Fact]
    public void SplitHeader_MissingKeys_AreNull()
    {
        var header = InvoiceRecognitionSplitter.SplitHeader(new Dictionary<string, string?>());
        Assert.All(InvoiceFields.HeaderFields, f => Assert.Null(header[f.Path]));
    }

    [Fact]
    public void SplitLineItems_ParsesJsonArray()
    {
        var values = new Dictionary<string, string?>
        {
            [InvoiceFields.LineItemsPath] = """[{"Наименование":"Кабель","Количество":"10"},{"Наименование":"Розетка","Количество":"5"}]""",
        };

        var rows = InvoiceRecognitionSplitter.SplitLineItems(values);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Кабель", rows[0]["Наименование"]);
        Assert.Equal("5", rows[1]["Количество"]);
    }

    [Fact]
    public void SplitLineItems_EmptyArray_ReturnsEmptyList()
    {
        var values = new Dictionary<string, string?> { [InvoiceFields.LineItemsPath] = "[]" };
        Assert.Empty(InvoiceRecognitionSplitter.SplitLineItems(values));
    }

    [Fact]
    public void SplitLineItems_MissingKey_ReturnsEmptyList()
    {
        Assert.Empty(InvoiceRecognitionSplitter.SplitLineItems(new Dictionary<string, string?>()));
    }

    [Fact]
    public void SplitLineItems_MalformedJson_ReturnsEmptyListInsteadOfThrowing()
    {
        var values = new Dictionary<string, string?> { [InvoiceFields.LineItemsPath] = "не json вообще" };
        Assert.Empty(InvoiceRecognitionSplitter.SplitLineItems(values));
    }

    [Fact]
    public void SplitLineItems_JsonObjectInsteadOfArray_ReturnsEmptyListInsteadOfThrowing()
    {
        var values = new Dictionary<string, string?> { [InvoiceFields.LineItemsPath] = """{"not":"an array"}""" };
        Assert.Empty(InvoiceRecognitionSplitter.SplitLineItems(values));
    }
}
