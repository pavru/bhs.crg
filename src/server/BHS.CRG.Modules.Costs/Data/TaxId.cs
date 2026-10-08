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
///
/// <para>Общее у сторон одно — как ИНН ВЫДЕЛЯЕТСЯ из текста (<see cref="Groups" />): и модель, и
/// человек пишут «7701234567/770101001». Читай стороны по-разному, запись с ИНН и КПП в одном поле не
/// совпала бы ни с чем, а ответом было бы «такой организации нет».</para>
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

        var (runs, fitting) = Groups(text);

        // Групп подходящей длины может быть несколько: «7701234567 (ИНН 7701234567)», ИНН и телефон.
        // Верим тем, у кого сходится контрольная сумма, — и только если такой ИНН один.
        var valid = fitting.Where(ChecksOut).ToList();
        if (valid.Count == 1) return valid[0];
        if (valid.Count > 1)
        {
            problem = $"«{text.Trim()}» — в поле несколько разных ИНН, и какой из них нужен, по скану не понять";
            return null;
        }

        // Ни одна группа не годится — возможно, ИНН разбит пробелами: пробуем все цифры подряд.
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
    /// ИНН записи справочника — цифрами; <c>null</c> — в поле цифр нет.
    ///
    /// <para>Выделяется так же, как из скана: единственная группа в 10 или 12 цифр — это он, что бы ни
    /// стояло рядом («7701234567/770101001», «7701234567.0» у числа). Иначе — все цифры подряд, и
    /// тогда 9 и 11 цифр дополняются нулём слева: число в JSON теряет ведущий ноль (ИНН Адыгеи
    /// начинается с «01»). Контрольная сумма здесь не проверяется.</para>
    /// </summary>
    public static string? FromRecord(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var (runs, fitting) = Groups(value);
        if (fitting.Count == 1) return fitting[0];

        var digits = string.Concat(runs);
        return digits.Length switch
        {
            0 => null,
            9 or 11 => "0" + digits,
            _ => digits,
        };
    }

    /// <summary>Группы цифр текста и те из них (без повторов), что длиной годятся в ИНН.</summary>
    private static (List<string> Runs, List<string> Fitting) Groups(string text)
    {
        var runs = Digits().Matches(text).Select(m => m.Value).ToList();
        return (runs, [.. runs.Where(r => r.Length is 10 or 12).Distinct()]);
    }

    private static bool ChecksOut(string digits) => digits.Length == 10
        ? Control(digits, Ten) == digits[9] - '0'
        : Control(digits, TwelveFirst) == digits[10] - '0' && Control(digits, TwelveSecond) == digits[11] - '0';

    private static int Control(string digits, int[] weights) =>
        weights.Select((weight, index) => weight * (digits[index] - '0')).Sum() % 11 % 10;
}
