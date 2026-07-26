using BHS.CRG.Infrastructure.Reconciliation;

namespace BHS.CRG.Tests.Reconciliation;

/// <summary>
/// Разбор количества. Ошибка здесь не косметическая: неверно прочитанное число даёт ложную находку —
/// ровно ту ошибку, ради предотвращения которой подсистема и делается.
/// </summary>
public class QuantityParserTests
{
    [Theory]
    // Русская запись: запятая — десятичный разделитель. Инвариантный TryParse, применяемый в фильтре
    // и сортировке, прочитал бы «125,5» как 1255.
    [InlineData("125,5", 125.5)]
    [InlineData("125.5", 125.5)]
    [InlineData("125", 125)]
    // Пробел (в т.ч. неразрывный) — разделитель разрядов.
    [InlineData("1 234,5", 1234.5)]
    [InlineData("1 234,5", 1234.5)]
    // Смешанная запись: десятичным считается последний разделитель.
    [InlineData("1,234.5", 1234.5)]
    [InlineData("1.234,5", 1234.5)]
    // Единицы пишут в той же ячейке — требовать чистого числа значило бы терять строки.
    [InlineData("125,5 м", 125.5)]
    [InlineData("12 шт.", 12)]
    [InlineData("-3,5", -3.5)]
    public void Parses(string raw, double expected)
    {
        Assert.True(QuantityParser.TryParse(raw, out var v));
        Assert.Equal(expected, v, 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("—")]
    [InlineData("нет данных")]
    public void RejectsNonNumeric(string? raw)
    {
        Assert.False(QuantityParser.TryParse(raw, out _));
        // Отличие «нет значения» от нуля существенно: ноль — заявленное количество, отсутствие — нет.
        Assert.Null(QuantityParser.Parse(raw));
    }

    [Fact]
    public void Zero_IsAValue_NotAbsence()
    {
        Assert.Equal(0, QuantityParser.Parse("0"));
        Assert.NotNull(QuantityParser.Parse("0"));
    }
}
