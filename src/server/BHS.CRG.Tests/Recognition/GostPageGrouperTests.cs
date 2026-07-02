using BHS.CRG.Infrastructure.Recognition;

namespace BHS.CRG.Tests.Recognition;

public class GostPageGrouperTests
{
    private static Dictionary<string, string?> Page(string? pageType, string? documentName = null, string? shifr = null) =>
        new()
        {
            [GostTitleBlockFields.PageTypePath] = pageType,
            ["НаименованиеДокумента"] = documentName,
            ["Шифр"] = shifr,
        };

    [Fact]
    public void RoutesCoverAndTitlePagePages()
    {
        var result = GostPageGrouper.Group([Page("Обложка"), Page("ТитульныйЛист"), Page("Документ", "Лист 1")]);

        Assert.Single(result.Cover);
        Assert.Single(result.TitlePage);
        Assert.Single(result.Documents);
    }

    [Fact]
    public void PageTypeKey_DoesNotLeakIntoOutputRows()
    {
        var result = GostPageGrouper.Group([Page("Обложка"), Page("Документ", "Лист 1")]);

        Assert.DoesNotContain(GostTitleBlockFields.PageTypePath, result.Cover[0].Keys);
        Assert.DoesNotContain(GostTitleBlockFields.PageTypePath, result.Documents[0].Fields.Keys);
    }

    [Fact]
    public void GroupsMultiplePagesByDocumentName_WithPageCount()
    {
        var pages = new[]
        {
            Page("Документ", "План этажа", shifr: "01-ЭМ"),
            Page("Документ", "План этажа", shifr: "01-ЭМ"),
            Page("Документ", "Разрез", shifr: "02-ЭМ"),
        };

        var result = GostPageGrouper.Group(pages);

        Assert.Equal(2, result.Documents.Count);
        var planEtazha = result.Documents.Single(d => d.DocumentName == "План этажа");
        Assert.Equal([0, 1], planEtazha.PageIndices);
        Assert.Equal("2", planEtazha.Fields["КоличествоЛистов"]);
        Assert.Equal("01-ЭМ", planEtazha.Fields["Шифр"]);

        var razrez = result.Documents.Single(d => d.DocumentName == "Разрез");
        Assert.Equal([2], razrez.PageIndices);
        Assert.Equal("1", razrez.Fields["КоличествоЛистов"]);
    }

    [Fact]
    public void EmptyDocumentName_FallsIntoDefaultBucket()
    {
        var result = GostPageGrouper.Group([Page("Документ", documentName: null), Page("Документ", documentName: "")]);

        var group = Assert.Single(result.Documents);
        Assert.Equal("(без названия)", group.DocumentName);
        Assert.Equal("2", group.Fields["КоличествоЛистов"]);
    }

    [Fact]
    public void UnknownOrMissingPageType_TreatedAsDocument()
    {
        var result = GostPageGrouper.Group([Page(pageType: null, documentName: "X"), Page(pageType: "что-то странное", documentName: "X")]);

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
            Page("Документ", "Лист", shifr: null),
            Page("Документ", "Лист", shifr: "05-АР"),
            Page("Документ", "Лист", shifr: "ДРУГОЕ"),
        };

        var result = GostPageGrouper.Group(pages);
        var group = Assert.Single(result.Documents);
        Assert.Equal("05-АР", group.Fields["Шифр"]);
    }
}
