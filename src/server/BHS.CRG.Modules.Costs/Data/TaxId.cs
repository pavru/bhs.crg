using System.Text.RegularExpressions;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>
/// ИНН — ключ, по которому распознанный счёт находит поставщика в справочнике (issue #1077).
///
/// <para>Правило знает модуль, а не ядро: для ядра «ИНН» — обычное текстовое поле записи, и что
/// считать одним и тем же ИНН, оно не решает.</para>
///
/// <para>⚠️ Две стороны читаются по-разному, и это нарочно. <b>Скан</b> проверяется строго: по ИНН,
/// прочитанному с ошибкой, поиск ответил бы «такой организации нет» — и человек завёл бы вторую.
/// Поэтому не 10 и не 12 цифр или несошедшаяся контрольная сумма — это названная причина, а не
/// поиск. <b>Справочник</b> принимается как есть: его вёл человек, и запись с опечаткой в ИНН —
/// запись, а не отказ; она просто ни с чем не совпадёт.</para>
/// </summary>
public static partial class TaxId
{
    private static readonly int[] Ten = [2, 4, 10, 3, 5, 9, 4, 6, 8];
    private static readonly int[] TwelveFirst = [7, 2, 4, 10, 3, 5, 9, 4, 6, 8];
    private static readonly int[] TwelveSecond = [3, 7, 2, 4, 10, 3, 5, 9, 4, 6, 8];

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    /// <summary>
    /// ИНН из текста скана; <c>null</c> — не прочитан, причина в <paramref name="problem" />.
    /// Пустой текст — не ошибка чтения, а «в скане его нет»: и значения нет, и причины нет.
    /// </summary>
    public static string? FromScan(string? text, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Модель нередко возвращает «7701234567/770101001» (ИНН/КПП) или ИНН с пробелами. Берём
        // единственную группу цифр подходящей длины; нет такой — все цифры подряд.
        var runs = Digits().Matches(text).Select(m => m.Value).ToList();
        var fitting = runs.Where(r => r.Length is 10 or 12).ToList();
        var digits = fitting.Count == 1 ? fitting[0] : string.Concat(runs);

        if (digits.Length is not (10 or 12))
        {
            problem = $"«{text.Trim()}» — не ИНН: в нём должно быть 10 цифр (организация) или 12 (предприниматель)";
            return null;
        }

        if (!ChecksOut(digits))
        {
            problem = $"«{digits}» прочитан с ошибкой: не сходится контрольная сумма ИНН";
            return null;
        }

        return digits;
    }

    /// <summary>
    /// ИНН записи справочника — цифрами. Число в JSON теряет ведущий ноль (ИНН Адыгеи начинается с
    /// «01»), поэтому 9 и 11 цифр дополняются нулём слева. <c>null</c> — в поле цифр нет.
    /// </summary>
    public static string? FromRecord(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var digits = string.Concat(Digits().Matches(value).Select(m => m.Value));
        return digits.Length switch
        {
            0 => null,
            9 or 11 => "0" + digits,
            _ => digits,
        };
    }

    private static bool ChecksOut(string digits) => digits.Length == 10
        ? Control(digits, Ten) == digits[9] - '0'
        : Control(digits, TwelveFirst) == digits[10] - '0' && Control(digits, TwelveSecond) == digits[11] - '0';

    private static int Control(string digits, int[] weights) =>
        weights.Select((weight, index) => weight * (digits[index] - '0')).Sum() % 11 % 10;
}
