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

    /// <summary>
    /// Пути частей из JSON — те же, что даёт полный разбор (ревью PR #1408): два чтения одного
    /// знания обязаны сходиться.
    /// </summary>
    [Fact]
    public void ReadPartPaths_MatchesFullParse()
    {
        var data = new GostGroupingData(
        [
            new GostGroupingGroup(GostGroupKind.Cover, null, null, [new GostGroupingPage(0, new Dictionary<string, string?>())]),
            new GostGroupingGroup(GostGroupKind.Document, "01-ЭМ", "Схема", [new GostGroupingPage(1, new Dictionary<string, string?>())],
                BlobPath: "bhs-crg/2026/10/17/00000000-0000-0000-0000-000000000001_01.pdf"),
            new GostGroupingGroup(GostGroupKind.Document, "02-ЭМ", "План", [new GostGroupingPage(2, new Dictionary<string, string?>())],
                BlobPath: ""),
        ], ManuallyEdited: false);
        var json = JsonSerializer.Serialize(data);

        Assert.Equal(GostGroupingSerialization.PartPaths(GostGroupingSerialization.Parse(json)),
            GostGroupingSerialization.ReadPartPaths(json));
    }

    /// <summary>
    /// Разбиение чужой формы путей не даёт и не роняет: на нём полный разбор падает исключениями,
    /// которых фильтр копии не ждал (ревью PR #1408). Прежний формат частей не знал вовсе.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("""{"Groups":null}""")]
    [InlineData("""{"Groups":[null, 1, "x", {"BlobPath": 5}]}""")]
    [InlineData("""{"Documents":[{"PageIndices":[1.5]}]}""")]
    public void ReadPartPaths_OddShape_IsEmpty(string? json)
    {
        Assert.Empty(GostGroupingSerialization.ReadPartPaths(json)!);
    }

    /// <summary>Не JSON или не объект — «не прочитано», а не «частей нет»: копия назовёт набор.</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("{не json")]
    public void ReadPartPaths_Unreadable_IsNull(string json)
    {
        Assert.Null(GostGroupingSerialization.ReadPartPaths(json));
    }
}
