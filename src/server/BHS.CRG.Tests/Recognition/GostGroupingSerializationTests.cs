using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Infrastructure.DataSets;

namespace BHS.CRG.Tests.Recognition;

/// <summary>Толерантная миграция формата GostGrouping (новый {Groups} + legacy {Documents}).</summary>
public class GostGroupingSerializationTests
{
    [Fact]
    public void Parse_Null_ReturnsNull()
    {
        Assert.Null(GostGroupingSerialization.Parse(null));
    }

    [Fact]
    public void Parse_NewGroupsFormat_RoundTrips()
    {
        var data = new GostGroupingData(
        [
            new GostGroupingGroup(GostGroupKind.Cover, null, null,
                [new GostGroupingPage(0, new Dictionary<string, string?> { ["Организация"] = "ООО А" })]),
            new GostGroupingGroup(GostGroupKind.Document, "01-ЭМ", "Схема",
                [new GostGroupingPage(1, new Dictionary<string, string?>())], ["gostDoc.specification"]),
        ], ManuallyEdited: true);
        var json = JsonSerializer.Serialize(data);

        var parsed = GostGroupingSerialization.Parse(json);

        Assert.NotNull(parsed);
        Assert.True(parsed!.ManuallyEdited);
        Assert.Equal(2, parsed.Groups.Count);
        Assert.Equal(GostGroupKind.Cover, parsed.Groups[0].Kind);
        var doc = parsed.Groups[1];
        Assert.Equal(GostGroupKind.Document, doc.Kind);
        Assert.Equal("01-ЭМ", doc.Code);
        Assert.Equal("Схема", doc.Name);
        Assert.Equal(1, doc.Pages[0].PageIndex);
        Assert.Equal(["gostDoc.specification"], doc.Tags!);
    }

    [Fact]
    public void Parse_LegacyDocumentsFormat_MapsToDocumentGroups()
    {
        // Старый формат до унифицированной модели: только Documents (без обложки/титула), Kind не задан.
        const string legacy = """
        {
          "ManuallyEdited": true,
          "Documents": [
            { "Code": "01-ЭМ", "Name": "Схема", "PageIndices": [0, 1] },
            { "Code": "02-ЭМ", "Name": null,    "PageIndices": [2] }
          ]
        }
        """;

        var parsed = GostGroupingSerialization.Parse(legacy);

        Assert.NotNull(parsed);
        Assert.True(parsed!.ManuallyEdited);
        Assert.Equal(2, parsed.Groups.Count);
        Assert.All(parsed.Groups, g => Assert.Equal(GostGroupKind.Document, g.Kind));
        Assert.Equal("01-ЭМ", parsed.Groups[0].Code);
        Assert.Equal("Схема", parsed.Groups[0].Name);
        Assert.Equal([0, 1], parsed.Groups[0].Pages.Select(p => p.PageIndex).ToArray());
        Assert.Null(parsed.Groups[1].Name);
        Assert.Equal([2], parsed.Groups[1].Pages.Select(p => p.PageIndex).ToArray());
    }

    [Fact]
    public void Parse_LegacyWithoutManuallyEdited_DefaultsFalse()
    {
        const string legacy = """{ "Documents": [ { "Code": "01", "PageIndices": [0] } ] }""";

        var parsed = GostGroupingSerialization.Parse(legacy);

        Assert.NotNull(parsed);
        Assert.False(parsed!.ManuallyEdited);
        Assert.Single(parsed.Groups);
    }
}
