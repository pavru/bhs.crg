using BHS.CRG.Application.Recognition;
using BHS.CRG.Infrastructure.Recognition;
using BHS.CRG.Modules.Costs;
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

    /// <summary>
    /// Переезд профиля от ядра к модулю содержимого не менял — и хеш обязан остаться прежним
    /// (issue #1077). Хеш здесь — тот, что стоял у строки «invoice» в базах до переезда.
    ///
    /// По хешу ядро решает, ушёл ли заводской вариант вперёд. Сдвинься он — у каждого, кто профиль
    /// правил, зажглось бы «заводской профиль обновился», а нетронутые строки перезаписались бы.
    /// Ломается правкой любой буквы в ключах или подсказках <see cref="CostsRecognitionProfiles" />;
    /// если правка намеренная, хеш здесь меняют вместе с ней — осознанно.
    /// </summary>
    [Fact]
    public void Hash_of_the_delivered_profile_did_not_move_with_its_owner()
    {
        RecognitionProfileDeclaration declaration = TestRecognition.Catalog.Find(CostsRecognitionProfiles.InvoiceCode)!;

        Assert.Equal("5FF9C0341A9ADEC855BDE0197F71FAEB09ED9F99E0D4C3491F1715EF81D926FB", declaration.Hash);
    }
}
