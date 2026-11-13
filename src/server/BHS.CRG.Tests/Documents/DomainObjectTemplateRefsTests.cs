using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;

namespace BHS.CRG.Tests.Documents;

/// <summary>
/// Уборка ссылок документа на шаблоны, которых нет (issue #1960): там, где отказать некому, — при
/// восстановлении копии, снятой до разовой чистки, и при удалении шаблона (его параметры).
/// </summary>
public class DomainObjectTemplateRefsTests
{
    private static readonly Guid Kept = Guid.NewGuid();
    private static readonly Guid Gone = Guid.NewGuid();

    private static DomainObject Doc(Guid? templateId, string? ids, string? pars) =>
        DomainObject.RestoreDocument(Guid.NewGuid(), Guid.NewGuid(), "АОСР", JsonDocument.Parse("{}"), Guid.NewGuid(),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], DocumentStatus.Draft, 0, templateId, ids, pars,
            JsonDocument.Parse("{}"));

    private static string Params(params Guid[] ids) =>
        new JsonObject(ids.Select(id => KeyValuePair.Create<string, JsonNode?>(id.ToString(), new JsonObject { ["x"] = 1 })))
            .ToJsonString();

    [Fact]
    public void ForgetUnknown_DropsAllThreeKinds_AndCountsThem()
    {
        var doc = Doc(Gone, JsonSerializer.Serialize(new[] { Gone, Kept }), Params(Kept, Gone));

        var removed = doc.ForgetUnknownTemplates(new HashSet<Guid> { Kept });

        Assert.Equal(3, removed);
        Assert.Null(doc.TemplateId);
        Assert.Equal([Kept], JsonSerializer.Deserialize<Guid[]>(doc.TemplateIds!));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Params(Kept)), JsonNode.Parse(doc.TemplateParams!)));
    }

    [Fact]
    public void ForgetUnknown_NothingLeft_IsNull()
    {
        var doc = Doc(null, JsonSerializer.Serialize(new[] { Gone }), Params(Gone));

        Assert.Equal(2, doc.ForgetUnknownTemplates(new HashSet<Guid> { Kept }));
        Assert.Null(doc.TemplateIds);
        Assert.Null(doc.TemplateParams);
    }

    [Fact]
    public void ForgetUnknown_AllKnown_TouchesNothing()
    {
        var ids = JsonSerializer.Serialize(new[] { Kept });
        var pars = Params(Kept);
        var doc = Doc(Kept, ids, pars);

        Assert.Equal(0, doc.ForgetUnknownTemplates(new HashSet<Guid> { Kept }));
        Assert.Equal(Kept, doc.TemplateId);
        Assert.Equal(ids, doc.TemplateIds);
        Assert.Equal(pars, doc.TemplateParams);
    }

    [Fact]
    public void ForgetUnknown_FlatOldParams_AreDropped()
    {
        // Старый плоский вид {имя:значение} генерация не читает: ключ — не шаблон.
        var doc = Doc(null, null, """{"x":1}""");

        Assert.Equal(1, doc.ForgetUnknownTemplates(new HashSet<Guid> { Kept }));
        Assert.Null(doc.TemplateParams);
    }

    [Fact]
    public void DropTemplateParams_RemovesOnlyThatTemplate()
    {
        var doc = Doc(null, null, Params(Kept, Gone));

        Assert.True(doc.DropTemplateParams(Gone));
        Assert.False(doc.DropTemplateParams(Gone));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Params(Kept)), JsonNode.Parse(doc.TemplateParams!)));
    }

    [Fact]
    public void ForgetUnknown_ElementsThatAreNotRefs_AreDropped_ValidKept()
    {
        // null, число и иной вид id — не ссылки; правило то же, что у SQL миграции (ревью PR #1963):
        // прежде null в массиве ронял разбор и стирал весь выбор.
        var doc = Doc(null, $"[null, 5, \"{Kept:N}\", \"{Kept.ToString().ToUpperInvariant()}\", \"{Kept}\"]", null);

        Assert.Equal(3, doc.ForgetUnknownTemplates(new HashSet<Guid> { Kept }));
        Assert.Equal([Kept], JsonSerializer.Deserialize<Guid[]>(doc.TemplateIds!));
    }

    [Fact]
    public void ForgetUnknown_UpperCaseParamKey_IsRewrittenCanonical_NotCounted()
    {
        var doc = Doc(null, null, $"{{\"{Kept.ToString().ToUpperInvariant()}\":{{\"x\":1}}}}");

        Assert.Equal(0, doc.ForgetUnknownTemplates(new HashSet<Guid> { Kept }));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Params(Kept)), JsonNode.Parse(doc.TemplateParams!)));
    }

    [Fact]
    public void ForgetUnknown_ParamKeyInOtherFormat_IsDropped()
    {
        var doc = Doc(null, null, $"{{\"{Kept:N}\":{{\"x\":1}}}}");

        Assert.Equal(1, doc.ForgetUnknownTemplates(new HashSet<Guid> { Kept }));
        Assert.Null(doc.TemplateParams);
    }

    [Fact]
    public void DuplicateParamKeys_DoNotThrow()
    {
        // JsonObject разбирает лениво: повтор ключа бросал ArgumentException мимо catch (JsonException).
        var dup = $"{{\"{Kept}\":{{}},\"{Kept}\":{{\"x\":1}}}}";

        Assert.False(Doc(null, null, dup).DropTemplateParams(Kept));
        var doc = Doc(null, null, dup);
        Assert.Equal(1, doc.ForgetUnknownTemplates(new HashSet<Guid> { Kept }));
        Assert.Null(doc.TemplateParams);
    }

    [Fact]
    public void DropTemplateParams_FindsUpperCaseKey()
    {
        var doc = Doc(null, null, $"{{\"{Gone.ToString().ToUpperInvariant()}\":{{\"x\":1}}}}");

        Assert.True(doc.DropTemplateParams(Gone));
        Assert.Null(doc.TemplateParams);
    }
}
