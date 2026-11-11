using BHS.CRG.Domain.Templates;

namespace BHS.CRG.Tests.Templates;

/// <summary>
/// Правило имени ассета-картинки (change core-template-asset-names, D2): простое имя файла — буквы
/// (кириллица тоже), цифры, пробел, «-», «_», «.»; без разделителей пути, без «..» и без пробела по краям.
/// </summary>
public class TemplateAssetNameRuleTests
{
    [Theory]
    [InlineData("img_0")]
    [InlineData("company-logo")]
    [InlineData("логотип-2.v1")]
    [InlineData("GOST2304A")]
    [InlineData("ГОСТ 21.101. Форма 3")]
    [InlineData("Логотип компании")]
    public void Простое_имя_проходит(string name) => Assert.True(TemplateAsset.IsValidImageName(name));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("fields/img_0")]
    [InlineData("a\b")]
    [InlineData("..")]
    [InlineData("a..b")]
    [InlineData(" logo")]
    [InlineData("logo ")]
    [InlineData("logo\tA")]
    [InlineData("logo(2)")]
    [InlineData("logo:png")]
    public void Иное_имя_не_проходит(string? name) => Assert.False(TemplateAsset.IsValidImageName(name));
}
