using BHS.CRG.Infrastructure.Recognition;

namespace BHS.CRG.Tests.Recognition;

public class GostPageGrouperTests
{
    private static Dictionary<string, string?> Page(string? pageType, string? shifr = null, string? documentName = null) =>
        new()
        {
            [GostTitleBlockFields.PageTypePath] = pageType,
            ["Шифр"] = shifr,
            ["НаименованиеДокумента"] = documentName,
        };

    [Fact]
    public void RoutesCoverAndTitlePagePages()
    {
        var result = GostPageGrouper.Group([Page("Обложка"), Page("ТитульныйЛист"), Page("Документ", "01-ЭМ")]);

        Assert.Single(result.Cover);
        Assert.Single(result.TitlePage);
        Assert.Single(result.Documents);
    }

    [Fact]
    public void PageTypeKey_DoesNotLeakIntoOutputRows()
    {
        var result = GostPageGrouper.Group([Page("Обложка"), Page("Документ", "01-ЭМ")]);

        Assert.DoesNotContain(GostTitleBlockFields.PageTypePath, result.Cover[0].Keys);
        Assert.DoesNotContain(GostTitleBlockFields.PageTypePath, result.Documents[0].Fields.Keys);
    }

    [Fact]
    public void GroupsMultiplePagesByShifr_WithPageCount()
    {
        var pages = new[]
        {
            Page("Документ", "01-ЭМ", "План этажа"),
            Page("Документ", "01-ЭМ", "План этажа"),
            Page("Документ", "02-ЭМ", "Разрез"),
        };

        var result = GostPageGrouper.Group(pages);

        Assert.Equal(2, result.Documents.Count);
        var planEtazha = result.Documents.Single(d => d.Code == "01-ЭМ");
        Assert.Equal([0, 1], planEtazha.PageIndices);
        Assert.Equal("2", planEtazha.Fields["КоличествоЛистов"]);
        Assert.Equal("План этажа", planEtazha.Fields["НаименованиеДокумента"]);

        var razrez = result.Documents.Single(d => d.Code == "02-ЭМ");
        Assert.Equal([2], razrez.PageIndices);
        Assert.Equal("1", razrez.Fields["КоличествоЛистов"]);
    }

    [Fact]
    public void EmptyShifr_FallsIntoDefaultBucket()
    {
        var result = GostPageGrouper.Group([Page("Документ", shifr: null), Page("Документ", shifr: "")]);

        var group = Assert.Single(result.Documents);
        Assert.Equal("(без шифра)", group.Code);
        Assert.Equal("2", group.Fields["КоличествоЛистов"]);
    }

    [Fact]
    public void UnknownOrMissingPageType_TreatedAsDocument()
    {
        var result = GostPageGrouper.Group([Page(pageType: null, shifr: "01-ЭМ"), Page(pageType: "что-то странное", shifr: "01-ЭМ")]);

        Assert.Empty(result.Cover);
        Assert.Empty(result.TitlePage);
        var group = Assert.Single(result.Documents);
        Assert.Equal("2", group.Fields["КоличествоЛистов"]);
    }

    [Fact]
    public void FirstNonEmptyValuePerField_IsKeptAcrossGroupPages()
    {
        var pages = new[]
        {
            Page("Документ", "01-ЭМ", documentName: null),
            Page("Документ", "01-ЭМ", documentName: "План этажа"),
            Page("Документ", "01-ЭМ", documentName: "ДРУГОЕ"),
        };

        var result = GostPageGrouper.Group(pages);
        var group = Assert.Single(result.Documents);
        Assert.Equal("План этажа", group.Fields["НаименованиеДокумента"]);
    }

    /// <summary>
    /// Регрессионный сценарий ГОСТ Р 21.101-2020: форма 5 (первый/титульный лист текстового
    /// документа) заполняет НаименованиеДокумента, форма 6 (последующие листы — как чертежей,
    /// так и текстовых документов) обычно НЕ повторяет наименование, но Шифр (графа 1) остаётся
    /// неизменным на всех листах. Группировка по Шифру должна корректно объединить такие страницы
    /// в один документ, а не развести титульный лист и продолжение по разным группам.
    /// </summary>
    [Fact]
    public void Form5FirstSheetAndForm6Continuation_GroupTogetherByMatchingShifr()
    {
        var pages = new[]
        {
            Page("Документ", shifr: "05-АР", documentName: "Пояснительная записка"), // форма 5
            Page("Документ", shifr: "05-АР", documentName: null),                     // форма 6, продолжение
            Page("Документ", shifr: "05-АР", documentName: null),                     // форма 6, продолжение
        };

        var result = GostPageGrouper.Group(pages);

        var group = Assert.Single(result.Documents);
        Assert.Equal("05-АР", group.Code);
        Assert.Equal([0, 1, 2], group.PageIndices);
        Assert.Equal("3", group.Fields["КоличествоЛистов"]);
        Assert.Equal("Пояснительная записка", group.Fields["НаименованиеДокумента"]);
    }
}
