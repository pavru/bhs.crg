using System.Text.Json;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Tests.Schema;

public class SchemaTagsTests
{
    private static DocumentType Type(string schemaJson, Guid? parentId = null,
        DocumentTypeKind kind = DocumentTypeKind.Document) =>
        DocumentType.Create("T", "C", kind, parentId, JsonDocument.Parse(schemaJson));

    [Fact]
    public void TaggedFields_ReturnsOwnTaggedFields()
    {
        var dt = Type("""
            {"fields":[
              {"key":"Стр","tags":["doc.pageCount"]},
              {"key":"Имя"}
            ]}
            """);
        var tags = SchemaTags.TaggedFields(dt, [dt]);
        Assert.Single(tags);
        Assert.Equal(("Стр", "doc.pageCount"), tags[0]);
    }

    [Fact]
    public void TaggedFields_MultipleTagsPerField()
    {
        var dt = Type("""{"fields":[{"key":"Арт","tags":["identity","doc.generatedBy"]}]}""");
        var tags = SchemaTags.TaggedFields(dt, [dt]);
        Assert.Equal(2, tags.Count);
        Assert.Contains(("Арт", "identity"), tags);
        Assert.Contains(("Арт", "doc.generatedBy"), tags);
    }

    [Fact]
    public void TaggedFields_InheritsFromParent_NearestWins()
    {
        var parent = Type("""{"fields":[{"key":"Дата","tags":["doc.generatedAt"]},{"key":"X","tags":["doc.generatedAt"]}]}""");
        var child = Type("""{"fields":[{"key":"X","tags":["doc.pageCount"]}]}""", parent.Id);
        var tags = SchemaTags.TaggedFields(child, [parent, child]);
        Assert.Contains(("Дата", "doc.generatedAt"), tags);
        Assert.Contains(("X", "doc.pageCount"), tags);     // ближний тип победил
        Assert.DoesNotContain(("X", "doc.generatedAt"), tags);
    }

    [Fact]
    public void FieldKeysWithTag_ReturnsMatching()
    {
        var dt = Type("""
            {"fields":[
              {"key":"Артикул","tags":["identity"]},
              {"key":"Наименование","tags":["identity"]},
              {"key":"Кач","tags":["material.qualityDocLink"]}
            ]}
            """);
        var ids = SchemaTags.FieldKeysWithTag(dt.Schema, "identity");
        Assert.Equal(["Артикул", "Наименование"], ids);
        Assert.Equal(["Кач"], SchemaTags.FieldKeysWithTag(dt.Schema, "material.qualityDocLink"));
    }

    // ── Параметр порядка у тэга (issue #583) ──────────────────────────────────

    /// <summary>
    /// Существующий функционал сравнивает тэг с константой и о параметрах не знает — поэтому тэг
    /// отдаётся КОДОМ. Иначе поле с номером молча выпало бы из сопоставления.
    /// </summary>
    [Fact]
    public void TaggedFields_StripParameterFromTag()
    {
        var dt = Type("""{"fields":[{"key":"Арт","tags":["identity:1"]}]}""");
        Assert.Equal(("Арт", "identity"), SchemaTags.TaggedFields(dt, [dt])[0]);
        Assert.Equal(["Арт"], SchemaTags.FieldKeysWithTag(dt.Schema, "identity"));
    }

    [Fact]
    public void SchemaHasTypeTag_IgnoresParameter()
    {
        var dt = Type("""{"fields":[],"tags":["type.union:2"]}""");
        Assert.True(SchemaTags.SchemaHasTypeTag(dt.Schema, "type.union"));
    }

    [Fact]
    public void OrderedKeysWithTag_NumberedFirst_ThenSchemaOrder()
    {
        var dt = Type("""
            {"fields":[
              {"key":"Наименование","tags":["identity"]},
              {"key":"Артикул","tags":["identity:1"]},
              {"key":"Производитель","tags":["identity:2"]},
              {"key":"Кач","tags":["material.qualityDocLink"]}
            ]}
            """);
        Assert.Equal(["Артикул", "Производитель", "Наименование"],
            SchemaTags.OrderedKeysWithTag(dt, [dt], "identity"));
    }

    /// <summary>
    /// Порядок полей — ЭФФЕКТИВНЫЙ (поля предка перед собственными), ровно как их показывает форма.
    /// Обход наследования идёт в обратную сторону (ближний тип первым, чтобы его тэги побеждали), и
    /// если бы порядком служил он, ключ связки, заведённой в UI, не совпал бы с ключом на генерации.
    /// </summary>
    [Fact]
    public void OrderedKeysWithTag_ParentFieldsBeforeOwn()
    {
        var parent = Type("""{"fields":[{"key":"Наименование","tags":["identity"]}]}""");
        var child = Type("""{"fields":[{"key":"МаркаКабеля","tags":["identity"]}]}""", parent.Id);
        Assert.Equal(["Наименование", "МаркаКабеля"],
            SchemaTags.OrderedKeysWithTag(child, [parent, child], "identity"));
    }

    /// <summary>Номер бьёт положение в схеме — иначе перестановка полей в редакторе меняла бы ключи.</summary>
    [Fact]
    public void OrderedKeysWithTag_NumberBeatsInheritanceDepth()
    {
        var parent = Type("""{"fields":[{"key":"Наименование","tags":["identity:2"]}]}""");
        var child = Type("""{"fields":[{"key":"МаркаКабеля","tags":["identity:1"]}]}""", parent.Id);
        Assert.Equal(["МаркаКабеля", "Наименование"],
            SchemaTags.OrderedKeysWithTag(child, [parent, child], "identity"));
    }

    [Fact]
    public void TypeHasTag_WalksInheritance()
    {
        var baseT = Type("""{"fields":[],"tags":["type.qualityDocument"]}""");
        var child = Type("""{"fields":[]}""", baseT.Id);
        Assert.True(SchemaTags.TypeHasTag(child, [baseT, child], "type.qualityDocument"));
        Assert.False(SchemaTags.TypeHasTag(baseT, [baseT, child], "type.unknown"));
    }

    [Fact]
    public void PatchMetadata_OverwritesAndAdds()
    {
        var requisites = JsonDocument.Parse("""{"Имя":"Док","Стр":0}""");
        var tagged = new List<(string, string)> { ("Стр", "doc.pageCount"), ("Дата", "doc.generatedAt") };
        var meta = new Dictionary<string, object?> { ["doc.pageCount"] = 7, ["doc.generatedAt"] = "2026-06-24" };

        var patched = SchemaTags.PatchMetadata(requisites, tagged, meta);
        var root = patched.RootElement;
        Assert.Equal(7, root.GetProperty("Стр").GetInt32());
        Assert.Equal("Док", root.GetProperty("Имя").GetString());
        Assert.Equal("2026-06-24", root.GetProperty("Дата").GetString());
    }

    [Fact]
    public void PatchMetadata_IgnoresTagWithoutMetaValue()
    {
        var requisites = JsonDocument.Parse("""{"Стр":3}""");
        var tagged = new List<(string, string)> { ("Стр", "doc.pageCount") };
        var patched = SchemaTags.PatchMetadata(requisites, tagged, new Dictionary<string, object?>());
        Assert.Equal(3, patched.RootElement.GetProperty("Стр").GetInt32());
    }
}
