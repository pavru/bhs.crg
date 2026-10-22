using System.Text.Json;
using BHS.CRG.Application.Objects.FieldSuggestions;

namespace BHS.CRG.Tests.Schema;

/// <summary>
/// Правило «поле подсказывается» — общий набор случаев с клиентом (issue #1529).
///
/// <para>Правило живёт в двух местах: здесь (<see cref="Suggestable.Reason" />) и в зеркале клиента
/// (<c>suggestReason</c> в <c>suggestable.ts</c>), — поле формы решает, звать ли адрес, не спрашивая
/// сервер. Случаи одни, в файле рядом с зеркалом, и считают их оба теста: отставший краснеет с
/// названием случая. Случаи порядка проверок стоят там же — поменять порядок на одной стороне нельзя.</para>
/// </summary>
public class SuggestableCasesTests
{
    private static readonly string CasesFile =
        RepoPaths.File("src", "client", "src", "shared", "api", "suggestableCases.json");

    public static TheoryData<string> Names() => [.. Cases().Select(c => c.GetProperty("name").GetString()!)];

    [Theory]
    [MemberData(nameof(Names))]
    public void Сервер_решает_как_клиент(string name)
    {
        var c = Cases().Single(x => x.GetProperty("name").GetString() == name);
        var field = c.GetProperty("field");

        var reason = Suggestable.Reason(
            field.GetProperty("type").GetString()!,
            field.GetProperty("computed").GetBoolean(),
            field.GetProperty("locked").GetBoolean(),
            [.. field.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!)]);

        Assert.Equal(c.GetProperty("expect").GetString(), reason);
    }

    /// <summary>
    /// Каждый код причины покрыт хотя бы одним случаем: код без случая расходился бы с зеркалом
    /// незамеченным.
    /// </summary>
    [Fact]
    public void Каждый_код_причины_есть_в_наборе()
    {
        var expected = Cases().Select(c => c.GetProperty("expect").GetString()).ToHashSet();
        string[] codes =
        [
            Suggestable.Yes, Suggestable.Type, Suggestable.Computed, Suggestable.Locked, Suggestable.Manual,
            .. Suggestable.UniqueTags.Select(u => u.Reason),
        ];

        Assert.All(codes, code => Assert.Contains(code, expected));
    }

    private static IEnumerable<JsonElement> Cases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(CasesFile));
        return [.. document.RootElement.GetProperty("cases").EnumerateArray().Select(c => c.Clone())];
    }
}
