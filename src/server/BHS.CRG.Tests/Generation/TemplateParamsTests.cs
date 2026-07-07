using BHS.CRG.Application.Generation;

namespace BHS.CRG.Tests.Generation;

/// <summary>Эффективные значения параметров шаблона: дефолты + переопределения, типизация.</summary>
public class TemplateParamsTests
{
    [Fact]
    public void Effective_DefaultsMergedWithOverrides_TypedByDeclaration()
    {
        const string template = """[{"name":"title","type":"string","default":"Проект"},{"name":"copies","type":"number","default":1},{"name":"draft","type":"boolean","default":false}]""";
        const string overrides = """{"title":"Итоговый","copies":3}""";

        var eff = TemplateParams.Effective(template, overrides);

        Assert.Equal("Итоговый", eff["title"]); // переопределено
        Assert.Equal(3d, eff["copies"]);         // переопределено, число
        Assert.Equal(false, eff["draft"]);       // дефолт, bool
    }

    [Fact]
    public void Effective_MissingOverride_UsesDefault()
    {
        const string template = """[{"name":"city","type":"string","default":"Владивосток"}]""";
        var eff = TemplateParams.Effective(template, "{}");
        Assert.Equal("Владивосток", eff["city"]);
    }

    [Fact]
    public void Effective_NoTemplateParameters_Empty()
    {
        Assert.Empty(TemplateParams.Effective(null, """{"x":1}"""));
        Assert.Empty(TemplateParams.Effective("", null));
    }

    [Fact]
    public void Effective_NumberFromStringOverride_Coerced()
    {
        const string template = """[{"name":"n","type":"number","default":0}]""";
        var eff = TemplateParams.Effective(template, """{"n":"42"}""");
        Assert.Equal(42d, eff["n"]);
    }

    [Fact]
    public void Effective_BrokenOverrideJson_FallsBackToDefaults()
    {
        const string template = """[{"name":"a","type":"string","default":"д"}]""";
        var eff = TemplateParams.Effective(template, "{ broken");
        Assert.Equal("д", eff["a"]);
    }

    [Fact]
    public void OverridesForTemplate_ExtractsPerTemplateEntry()
    {
        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();
        var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, string>>
        {
            [g1.ToString()] = new() { ["title"] = "A" },
            [g2.ToString()] = new() { ["title"] = "B" },
        });

        Assert.Contains("\"title\":\"A\"", TemplateParams.OverridesForTemplate(json, g1));
        Assert.Contains("\"title\":\"B\"", TemplateParams.OverridesForTemplate(json, g2));
        Assert.Null(TemplateParams.OverridesForTemplate(json, Guid.NewGuid())); // нет записи для шаблона
        Assert.Null(TemplateParams.OverridesForTemplate(null, g1));
    }

    [Fact]
    public void PerTemplateOverrides_AppliedIndividually()
    {
        var g1 = Guid.NewGuid();
        const string template = """[{"name":"title","type":"string","default":"деф"}]""";
        var instanceParams = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, string>>
        {
            [g1.ToString()] = new() { ["title"] = "переопределено" },
        });

        var eff = TemplateParams.Effective(template, TemplateParams.OverridesForTemplate(instanceParams, g1));
        Assert.Equal("переопределено", eff["title"]);

        // Для другого шаблона переопределений нет → дефолт.
        var other = TemplateParams.Effective(template, TemplateParams.OverridesForTemplate(instanceParams, Guid.NewGuid()));
        Assert.Equal("деф", other["title"]);
    }
}
