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
}
