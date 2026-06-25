using System.Text.Json;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Tests.Generation;

public class DocumentMetaTagHelperTests
{
    private static DocumentType Type(string schemaJson, Guid? parentId = null) =>
        DocumentType.Create("T", "C", DocumentTypeKind.Document, parentId, JsonDocument.Parse(schemaJson));

    [Fact]
    public void GetTaggedFields_ReturnsOwnTaggedFields()
    {
        var dt = Type("""
            {"fields":[
              {"key":"Стр","metaTag":"pageCount"},
              {"key":"Имя"}
            ]}
            """);
        var tags = DocumentMetaTagHelper.GetTaggedFields(dt, [dt]);
        Assert.Single(tags);
        Assert.Equal(("Стр", "pageCount"), tags[0]);
    }

    [Fact]
    public void GetTaggedFields_InheritsFromParent()
    {
        var parent = Type("""{"fields":[{"key":"Дата","metaTag":"generatedAt"}]}""");
        var child = Type("""{"fields":[{"key":"Стр","metaTag":"pageCount"}]}""", parent.Id);
        var tags = DocumentMetaTagHelper.GetTaggedFields(child, [parent, child]);
        Assert.Equal(2, tags.Count);
        Assert.Contains(("Дата", "generatedAt"), tags);
        Assert.Contains(("Стр", "pageCount"), tags);
    }

    [Fact]
    public void GetTaggedFields_NearestTypeWins()
    {
        var parent = Type("""{"fields":[{"key":"X","metaTag":"generatedAt"}]}""");
        var child = Type("""{"fields":[{"key":"X","metaTag":"pageCount"}]}""", parent.Id);
        var tags = DocumentMetaTagHelper.GetTaggedFields(child, [parent, child]);
        Assert.Single(tags);
        Assert.Equal(("X", "pageCount"), tags[0]);
    }

    [Fact]
    public void GetTaggedFields_NoFields_ReturnsEmpty()
    {
        var dt = Type("""{"fields":[{"key":"A"},{"key":"B"}]}""");
        Assert.Empty(DocumentMetaTagHelper.GetTaggedFields(dt, [dt]));
    }

    [Fact]
    public void PatchMetadata_OverwritesTaggedField()
    {
        var requisites = JsonDocument.Parse("""{"Имя":"Док","Стр":0}""");
        var tagged = new List<(string, string)> { ("Стр", "pageCount") };
        var meta = new Dictionary<string, object?> { ["pageCount"] = 7 };

        var patched = DocumentMetaTagHelper.PatchMetadata(requisites, tagged, meta);
        var root = patched.RootElement;
        Assert.Equal(7, root.GetProperty("Стр").GetInt32());
        Assert.Equal("Док", root.GetProperty("Имя").GetString());
    }

    [Fact]
    public void PatchMetadata_IgnoresTagWithoutMetaValue()
    {
        var requisites = JsonDocument.Parse("""{"Стр":3}""");
        var tagged = new List<(string, string)> { ("Стр", "pageCount") };
        var meta = new Dictionary<string, object?>(); // no pageCount

        var patched = DocumentMetaTagHelper.PatchMetadata(requisites, tagged, meta);
        Assert.Equal(3, patched.RootElement.GetProperty("Стр").GetInt32());
    }

    [Fact]
    public void PatchMetadata_AddsMissingKey()
    {
        var requisites = JsonDocument.Parse("""{"Имя":"Док"}""");
        var tagged = new List<(string, string)> { ("Дата", "generatedAt") };
        var meta = new Dictionary<string, object?> { ["generatedAt"] = "2026-06-24" };

        var patched = DocumentMetaTagHelper.PatchMetadata(requisites, tagged, meta);
        Assert.Equal("2026-06-24", patched.RootElement.GetProperty("Дата").GetString());
    }
}
