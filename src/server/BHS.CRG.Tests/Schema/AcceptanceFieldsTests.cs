using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Tests.Schema;

public class AcceptanceFieldsTests
{
    [Fact]
    public void Разбор_признака_одинаков_из_jsonb_и_запроса()
    {
        using var stored = JsonDocument.Parse("""
            { "fields": [{ "forAcceptance": true, "type": "string", "key": "акт" }] }
            """);
        using var request = JsonDocument.Parse("""{"fields":[{"key":"акт","type":"string","forAcceptance":true}]}""");
        Assert.Equal(DocumentTypeSchemaReader.OwnFields(stored), DocumentTypeSchemaReader.OwnFields(request));
        Assert.True(Assert.Single(DocumentTypeSchemaReader.OwnFields(request)).ForAcceptance);
        Assert.Empty(SchemaEditPolicy.Refusals(stored, request, SchemaEditLevel.Extendable, acceptanceFields: true));
    }

    [Theory]
    [InlineData(SchemaEditLevel.Extendable, true, "", null)]
    [InlineData(SchemaEditLevel.Extendable, true, ""","origin":"module" """, "поля модуля")]
    [InlineData(SchemaEditLevel.Extendable, true, ""","computed":true""", "расчётного")]
    [InlineData(SchemaEditLevel.Extendable, false, "", "нет приёмки")]
    [InlineData(SchemaEditLevel.Open, true, "", "нет приёмки")]
    [InlineData(SchemaEditLevel.Closed, true, "", "закрытом")]
    public void Признак_разрешён_только_своему_полю_поддерживаемого_типа(
        SchemaEditLevel level, bool supported, string extra, string? refusal)
    {
        using var old = JsonDocument.Parse("""{"fields":[{"key":"act","title":"Акт"}]}""");
        using var marked = JsonDocument.Parse($$"""{"fields":[{"key":"act","title":"Акт","forAcceptance":true{{extra}}}]}""");
        var refusals = SchemaEditPolicy.Refusals(old, marked, level, supported);
        if (refusal is null)
        {
            Assert.Empty(refusals);
            Assert.Empty(SchemaEditPolicy.Refusals(marked, old, level, supported));
        }
        else
        {
            Assert.Contains(refusals, reason => reason.Contains(refusal) && reason.Contains("Акт"));
        }
    }

    [Theory]
    [InlineData("{}", true)]
    [InlineData("{\"act\":null}", true)]
    [InlineData("{\"act\":\" \"}", true)]
    [InlineData("{\"act\":[]}", true)]
    [InlineData("{\"act\":{}}", true)]
    [InlineData("{\"act\":false}", false)]
    [InlineData("{\"act\":0}", false)]
    [InlineData("{\"act\":\"A-1\"}", false)]
    public void Пустота_общая_и_считаются_только_помеченные(string value, bool missing)
    {
        var fields = new AcceptanceFields([
            new("act", "string", null, "Акт", ForAcceptance: true),
            new("note", "string", null, "Примечание"),
        ]);
        using var data = JsonDocument.Parse(value);
        Assert.Equal(missing ? [new AcceptanceField("act", "Акт")] : Array.Empty<AcceptanceField>(),
            fields.Missing(data.RootElement));
    }
}
