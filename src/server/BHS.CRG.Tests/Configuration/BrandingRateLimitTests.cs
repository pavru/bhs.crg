using BHS.CRG.Api.Configuration;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Предел запросов оформления как настройка развёртывания.
///
/// Как и у предела резервной копии, проверяется поведение на негодном значении: незаданное берёт
/// предел поставки, заданное с ошибкой останавливает запуск. Иначе описка в deploy/.env молча
/// вернула бы 300 установке, где предел специально подняли, — и офис снова остался бы без шапки.
/// </summary>
public class BrandingRateLimitTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NotConfigured_FallsBackToDefault(string? value)
        => Assert.Equal(BrandingRateLimit.Default, BrandingRateLimit.Parse(value).PermitLimit);

    /// <summary>Значки и манифест следуют за поднятым пределом, иначе упёрлись бы в прежний.</summary>
    [Fact]
    public void Configured_IsUsed_AndIconsFollow()
    {
        var limit = BrandingRateLimit.Parse(" 3000 ");
        Assert.Equal(3000, limit.PermitLimit);
        Assert.Equal(3000 * BrandingRateLimit.IconsFactor, limit.IconsPermitLimit);
    }

    /// <summary>Умолчание даёт прежние числа обеих корзин — поставка не меняется.</summary>
    [Fact]
    public void Default_KeepsShippedLimits()
    {
        var limit = BrandingRateLimit.Parse(null);
        Assert.Equal(300, limit.PermitLimit);
        Assert.Equal(1200, limit.IconsPermitLimit);
    }

    [Theory]
    [InlineData("триста")]
    [InlineData("3000/5m")]
    [InlineData("1.5")]
    [InlineData("-300")]
    public void Unparsable_StopsStartup(string value)
        => Assert.Throws<InvalidOperationException>(() => BrandingRateLimit.Parse(value));

    /// <summary>
    /// Ниже поставки — без шапки окажутся те самые офисы, ради которых ключ заведён; выше потолка —
    /// предел, выключенный лишним нулём.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("299")]
    [InlineData("30001")]
    public void OutOfRange_StopsStartup(string value)
        => Assert.Throws<InvalidOperationException>(() => BrandingRateLimit.Parse(value));

    /// <summary>
    /// Одни цифры, но больше int, — описка лишними нулями. Отказ называет диапазон, а не «не число»:
    /// по второму человек ищет в строке невидимые знаки, а не лишний ноль.
    /// </summary>
    [Theory]
    [InlineData("30000000000")]
    [InlineData("300000000000000000000000000000")]
    public void DigitsBeyondInt_NamedAsOutOfRange(string value)
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => BrandingRateLimit.Parse(value));
        Assert.Contains("вне допустимого диапазона", refusal.Message);
    }

    [Fact]
    public void NotANumber_NamedAsSuch()
        => Assert.Contains("не число",
            Assert.Throws<InvalidOperationException>(() => BrandingRateLimit.Parse("3000/5m")).Message);

    [Theory]
    [InlineData("300")]
    [InlineData("30000")]
    public void Bounds_AreAccepted(string value)
        => Assert.Equal(int.Parse(value), BrandingRateLimit.Parse(value).PermitLimit);
}
