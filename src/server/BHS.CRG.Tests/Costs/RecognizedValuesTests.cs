using BHS.CRG.Modules.Costs.Data;

namespace BHS.CRG.Tests.Costs;

/// <summary>
/// Текст из скана → значение поля счёта (issue #1077). Проверяется главное свойство разбора: он
/// СТРОГ — всё, что не читается однозначно, даёт <c>null</c>, а не «что-нибудь похожее».
/// </summary>
public class RecognizedValuesTests
{
    [Theory]
    [InlineData("12.03.2026", 2026, 3, 12)]
    [InlineData("1.2.2026", 2026, 2, 1)]
    [InlineData("2026-03-12", 2026, 3, 12)]
    [InlineData("12 марта 2026 г.", 2026, 3, 12)]
    [InlineData("12 марта 2026 года", 2026, 3, 12)]
    [InlineData(" «12  марта 2026» ", 2026, 3, 12)]
    public void Дата_с_четырёхзначным_годом_читается(string text, int year, int month, int day) =>
        Assert.Equal(new DateOnly(year, month, day), RecognizedValues.Date(text));

    [Theory]
    [InlineData("01.02.26")]        // какой век?
    [InlineData("март 2026")]
    [InlineData("31.02.2026")]
    [InlineData("от 12.03.2026")]
    [InlineData("")]
    [InlineData(null)]
    public void Дата_которую_не_прочесть_однозначно_не_читается(string? text) =>
        Assert.Null(RecognizedValues.Date(text));

    [Theory]
    [InlineData("1234", "1234")]
    [InlineData("1 234,56", "1234.56")]
    [InlineData("1 234,56", "1234.56")]   // неразрывный пробел
    [InlineData("1234.5", "1234.5")]
    [InlineData("1.234,56", "1234.56")]
    [InlineData("1,234.56", "1234.56")]
    [InlineData("12 000,00 руб.", "12000.00")]
    [InlineData("500 ₽", "500")]
    [InlineData("-15,30", "-15.30")]
    public void Сумма_читается_когда_дробная_часть_однозначна(string text, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), RecognizedValues.Money(text));

    [Theory]
    [InlineData("1.234")]          // тысяча двести тридцать четыре или 1,234?
    [InlineData("1,234")]
    [InlineData("1.234.567")]
    [InlineData("12,34,56")]
    [InlineData("1.23,456.78")]
    [InlineData("12,")]
    [InlineData("двенадцать")]
    [InlineData("12 шт")]
    [InlineData("")]
    [InlineData(null)]
    public void Сумма_которую_не_прочесть_однозначно_не_читается(string? text) =>
        Assert.Null(RecognizedValues.Money(text));

    [Theory]
    [InlineData("0,125", "0.125")]
    [InlineData("1.234", "1.234")]     // у количества единственный разделитель — дробный
    [InlineData("1 500", "1500")]
    [InlineData("2,5", "2.5")]
    public void Количество_читает_единственный_разделитель_дробным(string text, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), RecognizedValues.Quantity(text));

    [Theory]
    [InlineData("1,2345678")]      // семь знаков — столько счёт не хранит
    [InlineData("1,2,3")]
    [InlineData("десять")]
    public void Количество_которое_не_прочесть_не_читается(string text) =>
        Assert.Null(RecognizedValues.Quantity(text));
}
