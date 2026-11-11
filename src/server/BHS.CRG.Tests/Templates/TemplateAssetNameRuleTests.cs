using BHS.CRG.Domain.Templates;

namespace BHS.CRG.Tests.Templates;

/// <summary>
/// Правило имени ассета-картинки (change core-template-asset-names, D2): простое имя файла — буквы
/// (кириллица тоже), цифры, «-», «_», «.»; без разделителей пути и без «..».
/// </summary>
public class TemplateAssetNameRuleTests
{
    [Theory]
    [InlineData("img_0")]
    [InlineData("company-logo")]
    [InlineData("логотип-2.v1")]
    [InlineData("GOST2304A")]
    public void Простое_имя_проходит(string name) => Assert.True(TemplateAsset.IsValidImageName(name));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("fields/img_0")]
    [InlineData("a\b")]
    [InlineData("..")]
    [InlineData("a..b")]
    [InlineData("Логотип компании")]
    [InlineData("logo(2)")]
    [InlineData("logo:png")]
    public void Иное_имя_не_проходит(string? name) => Assert.False(TemplateAsset.IsValidImageName(name));
}
