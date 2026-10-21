using System.Text.Json;
using BHS.CRG.Modules.Costs.Data;

namespace BHS.CRG.Tests.Costs;

/// <summary>
/// Приведение строки к ключу соответствия — общий набор примеров с окном выбора позиции (issue #1518).
///
/// <para>Окно само находит соседние строки счёта с тем же артикулом или наименованием (<c>matchKeyText</c>
/// в <c>supplierMatches.ts</c>) — тем же приведением, что <see cref="SupplierMatchKey.Normalize" />. Разойдись
/// они, окно предлагало бы «выбрана в строке 2» там, где сервер ключи различает, и наоборот. Примеры одни,
/// в файле рядом с окном; отставший тест краснеет.</para>
/// </summary>
public class SupplierKeyCasesTests
{
    private static readonly string CasesFile = RepoPaths.File("src", "client", "src", "features", "costs", "supplierKeyCases.json");

    public static TheoryData<string> Names() => [.. Cases().Select(c => c.GetProperty("name").GetString()!)];

    [Theory]
    [MemberData(nameof(Names))]
    public void Сервер_приводит_ключ_как_окно(string name)
    {
        var c = Cases().Single(x => x.GetProperty("name").GetString() == name);
        Assert.Equal(c.GetProperty("expect").GetString(), SupplierMatchKey.Normalize(c.GetProperty("input").GetString()!));
    }

    private static IEnumerable<JsonElement> Cases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(CasesFile));
        return [.. document.RootElement.GetProperty("cases").EnumerateArray().Select(c => c.Clone())];
    }
}
