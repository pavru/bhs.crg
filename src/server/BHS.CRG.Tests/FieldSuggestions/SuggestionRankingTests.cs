using BHS.CRG.Application.Objects.FieldSuggestions;

namespace BHS.CRG.Tests.FieldSuggestions;

/// <summary>
/// Порядок и отбор подсказок (issue #1530): сценарии спецификации core — без базы.
/// </summary>
public class SuggestionRankingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static SuggestionSeen Seen(string value, bool here, int minutes = 0, Guid? record = null) =>
        new(record ?? Guid.NewGuid(), value, here, T0.AddMinutes(minutes));

    private static string[] Values(IEnumerable<FieldSuggestion> items) => [.. items.Select(i => i.Value)];

    [Fact]
    public void Нормализация_регистр_ё_и_пробелы() =>
        Assert.Equal("елка пром", SuggestionRanking.Normalize("  Ёлка   Пром "));

    [Fact]
    public void Значение_равное_набранному_не_отдаётся() =>
        Assert.Empty(SuggestionRanking.Rank([Seen("Ёлка Пром", true), Seen("елка  пром", false)], "Елка пром", 8));

    [Fact]
    public void Близнецы_склеиваются_и_вместе_проходят_порог_чужих()
    {
        var items = SuggestionRanking.Rank([Seen("Ёлка Пром", false, 1), Seen("елка  пром", false, 2)], "ел", 8);

        var single = Assert.Single(items);
        Assert.False(single.Here);
    }

    [Fact]
    public void Совпадение_с_начала_слова()
    {
        Assert.Equal(["Ёлка Пром"], Values(SuggestionRanking.Rank([Seen("Ёлка Пром", true)], "пром", 8)));
        Assert.Empty(SuggestionRanking.Rank([Seen("Ёлка Пром", true)], "ром", 8));
    }

    [Fact]
    public void Граница_слова_не_только_пробел()
    {
        Assert.Equal(["ООО «Ромашка»"], Values(SuggestionRanking.Rank([Seen("ООО «Ромашка»", true)], "ром", 8)));
        Assert.Equal(["Альфа-Кабель"], Values(SuggestionRanking.Rank([Seen("Альфа-Кабель", true)], "кабель", 8)));
        // Внутри слова — по-прежнему нет, и первое вхождение внутри слова не прячет второе с начала.
        Assert.Empty(SuggestionRanking.Rank([Seen("Пром", true)], "ром", 8));
        Assert.Equal(["Хром-Ромб"], Values(SuggestionRanking.Rank([Seen("Хром-Ромб", true)], "ром", 8)));
    }

    [Fact]
    public void Порог_чужой_стройки_две_разные_записи()
    {
        var items = SuggestionRanking.Rank(
            [Seen("Бета", false), Seen("Гамма", false), Seen("Гамма", false)], "", 8);

        Assert.Equal([new FieldSuggestion("Гамма", false)], items);
    }

    [Fact]
    public void Частота_по_записям_а_не_по_вхождениям()
    {
        var record = Guid.NewGuid();
        Assert.Empty(SuggestionRanking.Rank([Seen("Гамма", false, record: record), Seen("Гамма", false, record: record)], "", 8));
    }

    [Fact]
    public void Своя_стройка_первой_даже_реже()
    {
        var items = SuggestionRanking.Rank(
            [Seen("Альфа", true), .. Enumerable.Range(0, 5).Select(i => Seen("Альтаир", false, i))], "аль", 8);

        Assert.Equal([new FieldSuggestion("Альфа", true), new FieldSuggestion("Альтаир", false)], items);
    }

    [Fact]
    public void Внутри_по_частоте_затем_по_свежести_и_предел()
    {
        var items = SuggestionRanking.Rank(
            [Seen("Редкий", true, 9), Seen("Частый", true, 1), Seen("Частый", true, 2),
             Seen("Свежий", true, 5), Seen("Старый", true, 3)], "", 3);

        Assert.Equal(["Частый", "Редкий", "Свежий"], Values(items));
    }

    [Fact]
    public void Написание_самое_частое_а_при_равенстве_самое_свежее()
    {
        Assert.Equal(["АББ"], Values(SuggestionRanking.Rank(
            [Seen("АББ", true, 1), Seen("АББ", true, 2), Seen("абб", true, 9)], "", 8)));
        Assert.Equal(["абб"], Values(SuggestionRanking.Rank([Seen("АББ", true, 1), Seen("абб", true, 9)], "", 8)));
    }

    [Fact]
    public void Пустые_значения_не_подсказываются() =>
        Assert.Empty(SuggestionRanking.Rank([Seen("   ", true), Seen("", true)], "", 8));
}
