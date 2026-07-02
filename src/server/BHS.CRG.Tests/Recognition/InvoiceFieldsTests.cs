using BHS.CRG.Infrastructure.Recognition;

namespace BHS.CRG.Tests.Recognition;

public class InvoiceFieldsTests
{
    [Fact]
    public void All_IsNotEmptyAndHasNoDuplicatePaths()
    {
        Assert.NotEmpty(InvoiceFields.All);
        var paths = InvoiceFields.All.Select(f => f.Path).ToList();
        Assert.Equal(paths.Count, paths.Distinct().Count());
    }

    [Theory]
    [InlineData("НомерСчёта")]
    [InlineData("ДатаСчёта")]
    [InlineData("Поставщик")]
    [InlineData("ИННПоставщика")]
    [InlineData("СуммаКОплате")]
    [InlineData("ВТомЧислеНДС")]
    public void All_ContainsExpectedHeaderFields(string path)
    {
        Assert.Contains(InvoiceFields.All, f => f.Path == path);
    }

    [Fact]
    public void All_ContainsLineItemsField()
    {
        Assert.Contains(InvoiceFields.All, f => f.Path == InvoiceFields.LineItemsPath);
    }

    [Fact]
    public void All_PathsHaveNoSpaces()
    {
        Assert.All(InvoiceFields.All, f => Assert.DoesNotContain(' ', f.Path));
    }

    [Fact]
    public void LineItemColumns_IsNotEmptyAndHasNoDuplicates()
    {
        Assert.NotEmpty(InvoiceFields.LineItemColumns);
        var paths = InvoiceFields.LineItemColumns.Select(f => f.Path).ToList();
        Assert.Equal(paths.Count, paths.Distinct().Count());
    }

    [Fact]
    public void HeaderFields_DoesNotIncludeLineItemsPath()
    {
        Assert.DoesNotContain(InvoiceFields.HeaderFields, f => f.Path == InvoiceFields.LineItemsPath);
    }
}
