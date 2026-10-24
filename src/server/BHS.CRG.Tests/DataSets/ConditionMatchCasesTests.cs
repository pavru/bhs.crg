using System.Text.Json;
using BHS.CRG.Application.Tables;

namespace BHS.CRG.Tests.DataSets;

/// <summary>
/// Сверка названий в колонке-перечне — общий набор случаев с клиентом (UI-56; change
/// <c>costs-filter-place-hints</c>).
///
/// <para>Экран помечает значение условия «нет в справочнике» своей копией правила
/// (<c>conditionMatch.ts</c>, <c>listMatches</c>): строка целиком, регистр снимается, ё и е различаются,
/// пробелы значимы. Копия мягче сервера промолчала бы там, где он не найдёт ничего; строже — обвинила бы
/// годное значение. Поэтому случаи одни, в файле рядом с клиентской копией, и считают их оба теста.</para>
/// </summary>
public class ConditionMatchCasesTests
{
    private static readonly string CasesFile = RepoPaths.File("src", "client", "src", "shared", "filter", "conditionMatchCases.json");

    public static TheoryData<string> Names() => [.. Cases().Select(c => c.GetProperty("name").GetString()!)];

    [Theory]
    [MemberData(nameof(Names))]
    public void Сервер_сверяет_перечень_как_экран(string name)
    {
        var c = Cases().Single(x => x.GetProperty("name").GetString() == name);
        var values = c.GetProperty("values").EnumerateArray().Select(v => v.GetString()!).ToList();
        var items = c.GetProperty("items").EnumerateArray().Select(v => v.GetString()!);

        Assert.Equal(c.GetProperty("expect").GetBoolean(),
            TableConditions.Matches(TableOperators.List, c.GetProperty("op").GetString()!, values, TableConditions.Join(items)));
    }

    private static IEnumerable<JsonElement> Cases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(CasesFile));
        return [.. document.RootElement.GetProperty("cases").EnumerateArray().Select(c => c.Clone())];
    }
}
