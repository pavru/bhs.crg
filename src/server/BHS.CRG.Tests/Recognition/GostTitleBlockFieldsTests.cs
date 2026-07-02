using BHS.CRG.Infrastructure.Recognition;

namespace BHS.CRG.Tests.Recognition;

public class GostTitleBlockFieldsTests
{
    [Fact]
    public void All_IsNotEmptyAndHasNoDuplicatePaths()
    {
        Assert.NotEmpty(GostTitleBlockFields.All);
        var paths = GostTitleBlockFields.All.Select(f => f.Path).ToList();
        Assert.Equal(paths.Count, paths.Distinct().Count());
    }

    [Theory]
    [InlineData("Шифр")]
    [InlineData("НомерЛиста")]
    [InlineData("ВсегоЛистов")]
    [InlineData("ОбъектСтроительства")]
    public void All_ContainsExpectedGostFields(string path)
    {
        Assert.Contains(GostTitleBlockFields.All, f => f.Path == path);
    }

    [Fact]
    public void All_PathsHaveNoSpaces()
    {
        // Path — JSON-ключ, который должен вернуть распознаватель; без пробелов надёжнее
        // (та же конвенция, что и у остальных RecognitionField в проекте — см. QualityDocs).
        Assert.All(GostTitleBlockFields.All, f => Assert.DoesNotContain(' ', f.Path));
    }

    [Fact]
    public void AllWithPageType_AppendsPageTypeFieldOnce()
    {
        Assert.Equal(GostTitleBlockFields.All.Count + 1, GostTitleBlockFields.AllWithPageType.Count);
        Assert.Contains(GostTitleBlockFields.AllWithPageType, f => f.Path == GostTitleBlockFields.PageTypePath);
        Assert.DoesNotContain(GostTitleBlockFields.All, f => f.Path == GostTitleBlockFields.PageTypePath);
    }

    [Fact]
    public void PageTypeField_HasThreeOptions()
    {
        Assert.Equal(["Обложка", "ТитульныйЛист", "Документ"], GostTitleBlockFields.PageTypeField.Options);
    }
}
