using System.Text.Json;
using BHS.CRG.Domain.Templates;

namespace BHS.CRG.Tests.Templates;

/// <summary>
/// Правило имени ассета-картинки (change core-template-asset-names, D2): имя файла, а не путь.
///
/// <para>Примеры — общий набор <c>templateAssetNameCases.json</c> рядом с клиентом: по нему же проверены
/// подсказка имени и отметка негодного ассета в панели (<c>templateAssetRef.test.ts</c>). Разойдись
/// правило на двух сторонах, подсказка предлагала бы имя, которое сервер отклонит сразу после выбора
/// файла, — отставший тест краснеет. Знаки, неудобные для строки C# (обратная косая, управляющие), в
/// JSON пишутся без двусмысленности.</para>
/// </summary>
public class TemplateAssetNameRuleTests
{
    private static readonly string CasesFile =
        RepoPaths.File("src", "client", "src", "shared", "api", "templateAssetNameCases.json");

    public static TheoryData<string> Valid() => [.. Read("valid")];
    public static TheoryData<string> Invalid() => [.. Read("invalid")];

    [Theory]
    [MemberData(nameof(Valid))]
    public void Имя_файла_проходит(string name) => Assert.True(TemplateAsset.IsValidImageName(name));

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Иное_имя_не_проходит(string name) => Assert.False(TemplateAsset.IsValidImageName(name));

    [Fact]
    public void Пустое_имя_не_проходит() => Assert.False(TemplateAsset.IsValidImageName(null));

    /// <summary>Набор не пуст и держит каждый запрещённый знак: иначе тест выше был бы зелёным впустую.</summary>
    [Fact]
    public void Набор_покрывает_каждый_запрещённый_знак()
    {
        var invalid = Read("invalid");
        foreach (var c in "/\\:*?\"<>|\t\0")
            Assert.Contains(invalid, n => n.Contains(c));
    }

    private static List<string> Read(string kind)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(CasesFile));
        return [.. doc.RootElement.GetProperty(kind).EnumerateArray().Select(e => e.GetString()!)];
    }
}
