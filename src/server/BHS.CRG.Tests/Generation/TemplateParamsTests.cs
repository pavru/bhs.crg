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
}
