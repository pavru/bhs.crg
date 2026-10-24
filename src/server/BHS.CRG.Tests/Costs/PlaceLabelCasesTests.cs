using System.Text.Json;
using BHS.CRG.Modules.Costs.Tables;

namespace BHS.CRG.Tests.Costs;

/// <summary>
/// Как реестр счетов называет места разноски — общий набор случаев с клиентом (ТЗ COST-20.1; change
/// <c>costs-filter-place-hints</c>).
///
/// <para>Отбор реестра идёт по названию, а подсказка в условии отбора собирает названия своей копией
/// (<c>registryWords.ts</c>). Разойдись слова, подсказка предлагала бы название, под которое не найдётся
/// ни одного счёта, а пометка «нет в справочнике» молчала бы о нём. Поэтому случаи одни, в файле рядом с
/// клиентской копией, и сверяют их оба теста: отставший краснеет.</para>
/// </summary>
public class PlaceLabelCasesTests
{
    private static readonly string CasesFile = RepoPaths.File("src", "client", "src", "features", "costs", "placeLabelCases.json");

    [Fact]
    public void Слова_реестра_совпадают_с_клиентом()
    {
        var words = Cases().GetProperty("words");
        Assert.Equal(words.GetProperty("noSection").GetString(), RegistryPlaceNames.NoSection);
        Assert.Equal(words.GetProperty("lostSection").GetString(), RegistryPlaceNames.LostSection);
        Assert.Equal(words.GetProperty("lostObject").GetString(), RegistryPlaceNames.LostObject);
    }

    [Fact]
    public void Подпись_раздела_совпадает_с_клиентом()
    {
        var sections = Cases().GetProperty("sections").EnumerateArray().ToList();
        // Положительный якорь: пустой набор прошёл бы проверку ниже, ничего не сверив.
        Assert.NotEmpty(sections);
        Assert.All(sections, c => Assert.Equal(
            c.GetProperty("label").GetString(),
            RegistryPlaceNames.SectionLabel(c.GetProperty("site").GetString()!, c.GetProperty("section").GetString()!)));
    }

    private static JsonElement Cases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(CasesFile));
        return document.RootElement.Clone();
    }
}
