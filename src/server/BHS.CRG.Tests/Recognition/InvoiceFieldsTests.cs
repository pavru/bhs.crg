using BHS.CRG.Infrastructure.Recognition;
using BHS.CRG.Tests.Support;

namespace BHS.CRG.Tests.Recognition;

/// <summary>
/// Заводской профиль «Счёт на оплату». Объявляет его модуль счетов (issue #1077); форму ответа —
/// ключ таблицы товаров — держит ядро.
/// </summary>
public class InvoiceFieldsTests
{
    [Fact]
    public void All_IsNotEmptyAndHasNoDuplicatePaths()
    {
        Assert.NotEmpty(TestRecognition.InvoiceCall);
        var paths = TestRecognition.InvoiceCall.Select(f => f.Path).ToList();
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
        Assert.Contains(TestRecognition.InvoiceCall, f => f.Path == path);
    }

    [Fact]
    public void All_ContainsLineItemsField()
    {
        Assert.Contains(TestRecognition.InvoiceCall, f => f.Path == InvoiceFields.LineItemsPath);
    }

    [Fact]
    public void All_PathsHaveNoSpaces()
    {
        Assert.All(TestRecognition.InvoiceCall, f => Assert.DoesNotContain(' ', f.Path));
    }

    [Fact]
    public void LineItemColumns_IsNotEmptyAndHasNoDuplicates()
    {
        Assert.NotEmpty(TestRecognition.InvoiceLines);
        var paths = TestRecognition.InvoiceLines.Select(f => f.Path).ToList();
        Assert.Equal(paths.Count, paths.Distinct().Count());
    }

    [Fact]
    public void HeaderFields_DoesNotIncludeLineItemsPath()
    {
        Assert.DoesNotContain(TestRecognition.InvoiceHeader, f => f.Path == InvoiceFields.LineItemsPath);
    }
}
