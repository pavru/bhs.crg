using BHS.CRG.Application.QualityDocs;

namespace BHS.CRG.Tests.QualityDocs;

public class MatchKeyNormalizerTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("Шт", "шт")]
    [InlineData("ШТ", "шт")]
    [InlineData("  шт  ", "шт")]                     // окружающие пробелы
    [InlineData("Шт.", "шт")]                        // хвостовая точка
    [InlineData("шт. ", "шт")]                       // точка + пробел
    [InlineData("шт...", "шт")]                      // повторяющиеся точки
    [InlineData("Провод  ВВГ   3х2.5", "провод ввг 3х2.5")] // схлоп внутренних пробелов, внутр. точка сохранена
    [InlineData("Провод ВВГ 3х2.5 ", "провод ввг 3х2.5")]   // хвостовой пробел
    public void Normalize_CanonicalForm(string? input, string expected)
        => Assert.Equal(expected, MatchKeyNormalizer.Normalize(input));

    [Fact]
    public void Normalize_TrailingDotAndPlain_AreEqual()
        => Assert.Equal(MatchKeyNormalizer.Normalize("Кабель."), MatchKeyNormalizer.Normalize("кабель"));
}
