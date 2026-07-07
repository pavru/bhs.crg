using BHS.CRG.Application.DataSets;
using BHS.CRG.Infrastructure.DataSets;

namespace BHS.CRG.Tests.Recognition;

public class GostGroupingProjectionTests
{
    private static GostGroupingPage Page(int idx, params (string, string?)[] fields) =>
        new(idx, fields.ToDictionary(f => f.Item1, f => f.Item2));

    [Fact]
    public void Project_CoverAndTitle_OneRowPerPage_FieldsAsIs()
    {
        var data = new GostGroupingData(
        [
            new GostGroupingGroup(GostGroupKind.Cover, null, null, [Page(0, ("Организация", "ООО А"))]),
            new GostGroupingGroup(GostGroupKind.TitlePage, null, null, [Page(1, ("Организация", "ООО Б"))]),
        ], ManuallyEdited: false);

        var rows = GostGroupingProjection.Project(data);

        Assert.Equal("ООО А", Assert.Single(rows.Cover)["Организация"]);
        Assert.Equal("ООО Б", Assert.Single(rows.TitlePage)["Организация"]);
        Assert.Empty(rows.Documents);
    }

    [Fact]
    public void Project_Document_AggregatesFirstNonEmpty_AndCountsSheets()
    {
        var data = new GostGroupingData(
        [
            new GostGroupingGroup(GostGroupKind.Document, "01-ЭМ", "План этажа",
            [
                Page(2, ("Шифр", "01-ЭМ"), ("Организация", null), ("НаименованиеДокумента", "План этажа")),
                Page(3, ("Шифр", "01-ЭМ"), ("Организация", "Институт"), ("НаименованиеДокумента", "ДРУГОЕ")),
            ]),
        ], ManuallyEdited: false);

        var doc = Assert.Single(GostGroupingProjection.Project(data).Documents);

        Assert.Equal("01-ЭМ", doc.Code);
        Assert.Equal("План этажа", doc.Name);
        Assert.Equal([2, 3], doc.PageIndices);
        Assert.Equal("2", doc.Fields["КоличествоЛистов"]);
        Assert.Equal("План этажа", doc.Fields["НаименованиеДокумента"]); // первое непустое
        Assert.Equal("Институт", doc.Fields["Организация"]);              // первое непустое (со 2-й страницы)
    }

    [Fact]
    public void Project_PreservesGroupOrder()
    {
        var data = new GostGroupingData(
        [
            new GostGroupingGroup(GostGroupKind.Document, "A", "A", [Page(0)]),
            new GostGroupingGroup(GostGroupKind.Document, "B", "B", [Page(1)]),
        ], ManuallyEdited: false);

        var docs = GostGroupingProjection.Project(data).Documents;

        Assert.Equal(["A", "B"], docs.Select(d => d.Code));
    }

    [Fact]
    public void Project_Empty_ReturnsEmpty()
    {
        var rows = GostGroupingProjection.Project(new GostGroupingData([], false));
        Assert.Empty(rows.Cover);
        Assert.Empty(rows.TitlePage);
        Assert.Empty(rows.Documents);
    }
}
