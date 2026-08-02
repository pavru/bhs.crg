using System.Text.Json;
using System.Text.RegularExpressions;

namespace BHS.CRG.Application.QualityDocs;

/// <summary>Как материал соотносится с областью продукции документа качества.</summary>
public enum ProductScopeVerdict
{
    /// <summary>Слова материала нашлись в описании продукции.</summary>
    Fits,
    /// <summary>Сравнивать нечем — у материала (или у документа) нет словесных признаков.</summary>
    Unverifiable,
    /// <summary>Слова ЕСТЬ, и ни одно не совпало. Только это и есть повод предупредить.</summary>
    Mismatched,
}

/// <summary>
/// Правдоподобие связки «материал → документ качества» по ОБЛАСТИ ПРОДУКЦИИ (issue #586).
///
/// Область действия сертификата лежит в реквизитах («Выключатели автоматические, торговой марки
/// EKF, модель: AV-125»), но с материалом не сверялась ничем. Следствие на живых данных: этот самый
/// сертификат на автоматы был привязан к 68 материалам, среди которых термоусаживаемые трубки,
/// светильники, короба, счётчик и клеммные коробки.
///
/// ЗЕРКАЛО клиентского <c>qualityMatch.ts</c> (issue #552): там та же оценка предупреждает при
/// массовой привязке, здесь — при проверке и выпуске документа. Правила совпадают намеренно, иначе
/// система предупреждала бы об одном, а выпускала другое. Пороги вымерены на 113 живых связках, их
/// обоснование — в комментариях клиентского модуля, дублировать не стоит.
///
/// Три исхода, а не «процент»: 58 из 113 живых связок дают ровно ноль, и почти все нули — артикулы
/// вроде <c>mb15-07-01m-54</c>, где сопоставлять просто нечего. Красить такой ноль тревогой значит
/// приучить человека игнорировать сигнал на самой аккуратной половине данных.
/// </summary>
public static class ProductScopeMatcher
{
    /// <summary>Материал подходит документу, если совпала хотя бы такая доля веса слов.</summary>
    public const double FitsThreshold = 0.34;

    /// <summary>Сколько словесных основ должно быть у САМОГО документа, чтобы по нему можно было
    /// судить: у нераспознанного («[PDF] Информационное письмо» с пустыми реквизитами) их две, и
    /// без этого порога «не похоже» получили бы ВСЕ материалы подряд, включая верные.</summary>
    private const int MinDocWords = 5;

    private static readonly HashSet<string> Stop =
        ["для", "или", "при", "без", "шт", "штук", "тип", "сертификат", "декларация", "соответствия"];

    private static readonly Regex NonWord = new("[^a-zа-я0-9]+", RegexOptions.Compiled);
    private static readonly Regex HasDigit = new(@"\d", RegexOptions.Compiled);
    private static readonly Regex VowelEnd = new("[аеиоуыюяьй]$", RegexOptions.Compiled);

    public static ProductScopeVerdict Assess(string materialText, IEnumerable<string> docTexts)
    {
        var hay = new HashSet<string>(Tokenize(string.Join(' ', docTexts)).Select(Stem));
        if (hay.Count(IsWordy) < MinDocWords) return ProductScopeVerdict.Unverifiable;

        var tokens = Weighted(materialText).Where(x => IsWordy(x.Token)).ToList();
        if (tokens.Count == 0) return ProductScopeVerdict.Unverifiable;

        return Relevance(tokens, hay) >= FitsThreshold ? ProductScopeVerdict.Fits : ProductScopeVerdict.Mismatched;
    }

    /// <summary>Все строковые значения JSON-объекта — «стог» для сопоставления (реквизиты документа
    /// приходят подмешанными в строку материала, отдельного запроса не нужно).</summary>
    public static void CollectStrings(JsonElement el, List<string> into)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                if (el.GetString() is { Length: > 0 } s) into.Add(s);
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) CollectStrings(item, into);
                break;
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject()) CollectStrings(prop.Value, into);
                break;
        }
    }

    // ── разбор текста: буква-в-букву как в qualityMatch.ts ──────────────────────

    private static string NormText(string s)
        => NonWord.Replace(s.ToLowerInvariant().Replace('ё', 'е'), " ").Trim();

    private static IEnumerable<string> Tokenize(string s)
        => NormText(s).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3 && !Stop.Contains(t));

    /// <summary>Грубая основа слова: снимаем одну конечную гласную (или мягкий знак) и ограничиваем
    /// длину. Без снятия окончания «кабель» и «кабели» остаются разными словами — а именно короткие
    /// существительные и составляют номенклатуру.</summary>
    private static string Stem(string t)
    {
        var b = t.Length >= 5 && VowelEnd.IsMatch(t) ? t[..^1] : t;
        return b.Length > 6 ? b[..6] : b;
    }

    /// <summary>Слово, по которому вообще можно судить: чисто буквенное и не короткое. Артикул
    /// <c>mb15-07-01m-54</c> распадается на «слова» вроде <c>mb15</c> — они выглядят токенами, но ни
    /// один сертификат их не содержит.</summary>
    private static bool IsWordy(string token) => token.Length >= 4 && !HasDigit.IsMatch(token);

    private static List<(string Token, int Weight)> Weighted(string query)
    {
        var seen = new HashSet<string>();
        var result = new List<(string, int)>();
        foreach (var t in Tokenize(query))
        {
            if (!seen.Add(t)) continue;
            // числа/модели и длинные слова важнее общих коротких
            result.Add((t, HasDigit.IsMatch(t) ? 3 : t.Length >= 6 ? 2 : 1));
        }
        return result;
    }

    private static double Relevance(IReadOnlyList<(string Token, int Weight)> tokens, HashSet<string> hayStems)
    {
        var matched = 0;
        var total = 0;
        foreach (var (token, weight) in tokens)
        {
            total += weight;
            if (hayStems.Contains(Stem(token))) matched += weight;
        }
        return total == 0 ? 0 : (double)matched / total;
    }
}
