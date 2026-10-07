using BHS.CRG.Tests.Support;
using BHS.CRG.Infrastructure.Recognition;

namespace BHS.CRG.Tests.Recognition;

public class GostTitleBlockFieldsTests
{
    [Fact]
    public void All_IsNotEmptyAndHasNoDuplicatePaths()
    {
        Assert.NotEmpty(TestRecognition.TitleBlock);
        var paths = TestRecognition.TitleBlock.Select(f => f.Path).ToList();
        Assert.Equal(paths.Count, paths.Distinct().Count());
    }

    [Theory]
    [InlineData("Шифр")]
    [InlineData("НомерЛиста")]
    [InlineData("ВсегоЛистов")]
    [InlineData("ОбъектСтроительства")]
    public void All_ContainsExpectedGostFields(string path)
    {
        Assert.Contains(TestRecognition.TitleBlock, f => f.Path == path);
    }

    [Fact]
    public void All_PathsHaveNoSpaces()
    {
        // Path — JSON-ключ, который должен вернуть распознаватель; без пробелов надёжнее
        // (та же конвенция, что и у остальных RecognitionField в проекте — см. QualityDocs).
        Assert.All(TestRecognition.TitleBlock, f => Assert.DoesNotContain(' ', f.Path));
    }

    [Fact]
    public void WithClassifiers_AppendsBothClassifierFieldsOnce()
    {
        Assert.Equal(TestRecognition.TitleBlock.Count + 2, GostTitleBlockFields.WithClassifiers(TestRecognition.TitleBlock).Count);
        Assert.Contains(GostTitleBlockFields.WithClassifiers(TestRecognition.TitleBlock), f => f.Path == GostTitleBlockFields.PageTypePath);
        Assert.Contains(GostTitleBlockFields.WithClassifiers(TestRecognition.TitleBlock), f => f.Path == GostTitleBlockFields.StampFormPath);
        Assert.DoesNotContain(TestRecognition.TitleBlock, f => f.Path == GostTitleBlockFields.PageTypePath);
        Assert.DoesNotContain(TestRecognition.TitleBlock, f => f.Path == GostTitleBlockFields.StampFormPath);
    }

    [Fact]
    public void PageTypeField_HasThreeOptions()
    {
        Assert.Equal(["Обложка", "ТитульныйЛист", "Документ"], GostTitleBlockFields.PageTypeField.Options);
    }

    [Fact]
    public void StampFormField_HasFourOptions()
    {
        Assert.Equal(["Форма3", "Форма4", "Форма5", "Форма6"], GostTitleBlockFields.StampFormField.Options);
    }
}
