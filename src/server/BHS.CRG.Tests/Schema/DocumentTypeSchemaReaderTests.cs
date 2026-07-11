using System.Text.Json;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Tests.Schema;

/// <summary>
/// Резолв enum-вариантов (issue #59): легаси инлайн options (код==имя) и typeId → EnumType.Values —
/// оба представления толерантно сосуществуют, downstream получает одинаковую форму Options.
/// </summary>
public class DocumentTypeSchemaReaderTests
{
    private static DocumentType Type(string schemaJson) =>
        DocumentType.Create("T", "C", DocumentTypeKind.Document, null, JsonDocument.Parse(schemaJson));

    private static EnumType Enum(string valuesJson) =>
        EnumType.Create("Статус", "STATUS", null, JsonDocument.Parse(valuesJson));

    [Fact]
    public void EffectiveFields_LegacyInlineOptions_ResolvesCodeEqualsLabel()
    {
        var dt = Type("""{"fields":[{"key":"status","type":"enum","options":["Черновик","Согласован"]}]}""");
        var fields = DocumentTypeSchemaReader.EffectiveFields(dt.Id, new Dictionary<Guid, DocumentType> { [dt.Id] = dt });

        var field = Assert.Single(fields);
        Assert.NotNull(field.Options);
        Assert.Equal(2, field.Options!.Count);
        Assert.Equal(new EnumOptionInfo("Черновик", "Черновик"), field.Options[0]);
        Assert.Equal(new EnumOptionInfo("Согласован", "Согласован"), field.Options[1]);
    }

    [Fact]
    public void EffectiveFields_TypeIdWithEnumTypesById_ResolvesCodeLabelPairs()
    {
        var enumType = Enum("""[{"code":"DRAFT","label":"Черновик"},{"code":"APPROVED","label":"Согласован"}]""");
        var dt = Type($$"""{"fields":[{"key":"status","type":"enum","typeId":"{{enumType.Id}}"}]}""");

        var fields = DocumentTypeSchemaReader.EffectiveFields(dt.Id, new Dictionary<Guid, DocumentType> { [dt.Id] = dt },
            new Dictionary<Guid, EnumType> { [enumType.Id] = enumType });

        var field = Assert.Single(fields);
        Assert.NotNull(field.Options);
        Assert.Equal(new EnumOptionInfo("DRAFT", "Черновик"), field.Options![0]);
        Assert.Equal(new EnumOptionInfo("APPROVED", "Согласован"), field.Options[1]);
    }

    [Fact]
    public void EffectiveFields_TypeIdWithoutEnumTypesById_OptionsIsNull()
    {
        // enumTypesById не передан вызывающим кодом (напр. call site, которому Options не нужен) —
        // не должно падать, просто Options остаётся null.
        var dt = Type($$"""{"fields":[{"key":"status","type":"enum","typeId":"{{Guid.NewGuid()}}"}]}""");
        var fields = DocumentTypeSchemaReader.EffectiveFields(dt.Id, new Dictionary<Guid, DocumentType> { [dt.Id] = dt });

        Assert.Null(Assert.Single(fields).Options);
    }

    [Fact]
    public void EffectiveFields_NonEnumField_OptionsIsNull()
    {
        var dt = Type("""{"fields":[{"key":"name","type":"string"}]}""");
        var fields = DocumentTypeSchemaReader.EffectiveFields(dt.Id, new Dictionary<Guid, DocumentType> { [dt.Id] = dt });

        Assert.Null(Assert.Single(fields).Options);
    }

    [Fact]
    public void ReferencesEnumType_MatchesFieldWithSameTypeId()
    {
        var enumTypeId = Guid.NewGuid();
        var dt = Type($$"""{"fields":[{"key":"status","type":"enum","typeId":"{{enumTypeId}}"}]}""");

        Assert.True(DocumentTypeSchemaReader.ReferencesEnumType(dt.Schema, enumTypeId));
        Assert.False(DocumentTypeSchemaReader.ReferencesEnumType(dt.Schema, Guid.NewGuid()));
    }
}
