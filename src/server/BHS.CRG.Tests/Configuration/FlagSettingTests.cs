using BHS.CRG.Modules.Settings;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Настройка-флаг (задача C4, issue #1080): хранится словами «true» / «false», человеку называется
/// словами самой настройки.
/// </summary>
public class FlagSettingTests
{
    private static readonly FlagSetting Flag = new(
        "costs.due.flag", "Флаг", "Что меняет", DefaultValue: true, On: "считать так", Off: "считать иначе");

    [Theory]
    [InlineData("true", "true")]
    [InlineData("True", "true")]
    [InlineData(" FALSE ", "false")]
    public void Значение_хранится_строчными(string value, string stored)
    {
        Assert.Null(Flag.Refuse(value));
        Assert.Equal(stored, Flag.Normalize(value));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("да")]
    [InlineData("")]
    public void Не_флаг_отвергается(string value) =>
        Assert.Equal("нужно «true» или «false»", Flag.Refuse(value));

    [Fact]
    public void Человеку_значение_называется_словами_настройки()
    {
        Assert.Equal("считать так", Flag.Display("true"));
        Assert.Equal("считать иначе", Flag.Display("false"));
    }

    [Fact]
    public void Негодное_сохранённое_читается_умолчанием()
    {
        Assert.True(Flag.Read("1"));
        Assert.True(Flag.Read(null));
        Assert.False(Flag.Read("false"));
    }

    [Fact]
    public void Объявление_без_названия_значения_негодно() =>
        Assert.Contains("оба значения", (Flag with { Off = " " }).Validate());

    [Fact]
    public void Объявленное_модулем_годно() =>
        Assert.Null(BHS.CRG.Modules.Costs.CostsSettings.ShippingDayCounts.Validate());
}
