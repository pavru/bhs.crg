using System.Text;
using System.Text.RegularExpressions;

namespace BHS.CRG.Infrastructure.Reconciliation;

/// <summary>
/// Доменный ключ находки — высший продуктовый риск подсистемы (P2 в issue #414).
///
/// Ключ обязан строиться из СОДЕРЖАНИЯ строки (марка, сечение, наименование), а не из её порядкового
/// номера: перенумерация 1..N в этих документах происходит регулярно, и ключ на номере обнулял бы всю
/// накопленную память о принятых решениях при первой же вставке строки. Именно это делает журнал
/// сверки ценным артефактом, а не разовым отчётом.
/// </summary>
public static class ReconciliationKeys
{
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Нормализация одного значения ключа: схлопнуть пробелы, привести регистр, унифицировать тире и
    /// десятичный разделитель. Всё это — варианты записи ОДНОГО и того же, различающиеся между
    /// документами: «ВВГнг(А)-LS 3х2,5» и «ВВГнг(А)–LS 3Х2.5» обязаны дать один ключ, иначе одна
    /// позиция превратится в две находки-сироты.
    /// </summary>
    public static string NormalizePart(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw.Trim())
            sb.Append(c switch
            {
                '‐' or '‑' or '‒' or '–' or '—' or '−' => '-', // тире всех видов
                ' ' => ' ',
                ',' => '.',
                _ => c,
            });

        return Spaces.Replace(sb.ToString(), " ").ToLowerInvariant();
    }

    /// <summary>Составной ключ из нескольких колонок. Разделитель непечатный, чтобы значение с ним
    /// внутри не склеило два разных ключа в один.</summary>
    public static string Build(IEnumerable<string?> parts)
        => string.Join('', parts.Select(NormalizePart));

    /// <summary>Пустой ключ (все составляющие пусты) — строка не участвует в сверке: сопоставлять
    /// нечего, и такая «находка» была бы шумом, а не расхождением.</summary>
    public static bool IsEmpty(string key) => key.Replace('', ' ').Trim().Length == 0;
}
