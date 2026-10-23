using System.Text.RegularExpressions;

namespace BHS.CRG.Application.Objects.FieldSuggestions;

/// <summary>Значение поля одной записи окна: как набрано, своя ли стройка и когда правлена запись.</summary>
public sealed record SuggestionSeen(Guid RecordId, string Value, bool Here, DateTimeOffset UpdatedAt);

/// <summary>Пункт подсказки — ровно то, что уходит на провод (D10): значение и «своя стройка».</summary>
public sealed record FieldSuggestion(string Value, bool Here);

/// <summary>
/// Порядок и отбор подсказок — ЧИСТАЯ функция над прочитанным окном (issue #1530, D8, D10).
///
/// <para>Удаления «ошибочной подсказки» нет (D8): вместо второго источника истины — порог и склейка.
/// Опечатка, набранная однажды на чужой стройке, порог не проходит; исправленная в записи — уходит
/// сама.</para>
/// </summary>
public static partial class SuggestionRanking
{
    /// <summary>Сколько разных записей другой стройки нужно значению, чтобы его предложить.</summary>
    public const int ForeignThreshold = 2;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>
    /// Ключ сравнения: нижний регистр, «ё» как «е», пробелы схлопнуты и обрезаны. «Ёлка  Пром» и
    /// «елка пром» — одно значение, набранное по-разному, а не два.
    /// </summary>
    public static string Normalize(string value) =>
        Spaces().Replace(value.ToLowerInvariant().Replace('ё', 'е'), " ").Trim();

    /// <summary>
    /// Подсказки по набранному <paramref name="q" />, не больше <paramref name="limit" />.
    ///
    /// <para>Совпадение — с начала любого слова ключа: «пром» находит «Ёлка Пром», «ром» — нет, а
    /// «ром» находит «ООО «Ромашка»» (граница слова — не только пробел).
    /// Значение, равное набранному целиком, не отдаётся: человек его уже ввёл. Пустой набор — без
    /// отбора, самые частые.</para>
    ///
    /// <para>Частота — число РАЗНЫХ записей, отдельно своих и чужих. Свои проходят от одной записи,
    /// чужие — от двух (<see cref="ForeignThreshold" />). Свои стоят первыми; внутри — по частоте, затем
    /// по свежести. Показывается самое частое написание близнецов, при равенстве — самой свежей
    /// записи.</para>
    /// </summary>
    public static IReadOnlyList<FieldSuggestion> Rank(IEnumerable<SuggestionSeen> seen, string? q, int limit)
    {
        var typed = Normalize(q ?? "");
        return seen
            .Select(s => (Seen: s, Key: Normalize(s.Value)))
            .Where(x => x.Key.Length > 0 && x.Key != typed && Matches(x.Key, typed))
            .GroupBy(x => x.Key, x => x.Seen)
            .Select(Summarize)
            .Where(g => g.Here || g.Foreign >= ForeignThreshold)
            .OrderByDescending(g => g.Here)
            .ThenByDescending(g => g.Here ? g.Own : g.Foreign)
            .ThenByDescending(g => g.Freshest)
            .ThenBy(g => g.Value, StringComparer.Ordinal)
            .Take(limit)
            .Select(g => new FieldSuggestion(g.Value, g.Here))
            .ToList();
    }

    /// <summary>
    /// Набранное стоит в ключе с начала слова. Граница слова — любой знак, не буква и не цифра, а не
    /// только пробел: производители и организации пишутся в кавычках и через дефис, и «ром» обязан
    /// находить «ООО «Ромашка»», а «кабель» — «Альфа-Кабель».
    /// </summary>
    private static bool Matches(string key, string typed)
    {
        if (typed.Length == 0) return true;
        for (var at = key.IndexOf(typed, StringComparison.Ordinal); at >= 0;
             at = key.IndexOf(typed, at + 1, StringComparison.Ordinal))
            if (at == 0 || !char.IsLetterOrDigit(key[at - 1])) return true;
        return false;
    }

    private sealed record Group(string Value, bool Here, int Own, int Foreign, DateTimeOffset Freshest);

    private static Group Summarize(IEnumerable<SuggestionSeen> twins)
    {
        var all = twins.ToList();
        var own = all.Where(s => s.Here).Select(s => s.RecordId).Distinct().Count();
        var foreign = all.Where(s => !s.Here).Select(s => s.RecordId).Distinct().Count();
        var spelling = all
            .GroupBy(s => s.Value, StringComparer.Ordinal)
            .OrderByDescending(g => g.Select(s => s.RecordId).Distinct().Count())
            .ThenByDescending(g => g.Max(s => s.UpdatedAt))
            .First().Key;
        return new Group(spelling, own > 0, own, foreign, all.Max(s => s.UpdatedAt));
    }
}
