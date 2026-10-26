using BHS.CRG.Api.Configuration;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Предел входов как настройка (plan-09): незаданное — поставка, 30; поднять можно, опустить ниже поставки и выключить
/// лишним нулём — нельзя, описка останавливает запуск.
/// </summary>
public class LoginRateLimitTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NotConfigured_KeepsShippedLimit(string? value)
        => Assert.Equal(30, LoginRateLimit.Parse(value).PermitLimit);

    [Theory]
    [InlineData("30")]
    [InlineData(" 60 ")]
    [InlineData("600")]
    public void Configured_IsUsed(string value)
        => Assert.Equal(int.Parse(value), LoginRateLimit.Parse(value).PermitLimit);

    [Theory]
    [InlineData("29")]
    [InlineData("601")]
    [InlineData("60000000000")]
    public void OutOfRange_StopsStartup(string value)
        => Assert.Contains("вне допустимого диапазона",
            Assert.Throws<InvalidOperationException>(() => LoginRateLimit.Parse(value)).Message);

    [Theory]
    [InlineData("шестьдесят")]
    [InlineData("-60")]
    public void NotANumber_StopsStartup(string value)
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => LoginRateLimit.Parse(value));
        Assert.Contains("не число", refusal.Message);
        Assert.Contains("RateLimits__Login", refusal.Message);
    }
}
