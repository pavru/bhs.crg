using System.Globalization;
using System.Text.RegularExpressions;

namespace BHS.CRG.Infrastructure.Common;

/// <summary>
/// Разбор количества из ячейки набора данных — общий для сверки и для привязки наборов (#466).
///
/// Общий намеренно: одна и та же ячейка обязана читаться одинаково и когда значение кладётся в
/// документ, и когда оно сравнивается при сверке. Разойдись эти два разбора — документ и отчёт
/// показали бы разные числа по одним данным.
///
/// Существующий приём фильтра и сортировки — <c>double.TryParse(NumberStyles.Any, InvariantCulture)</c> —
/// для сверки не годится: в инвариантной культуре запятая это разделитель тысяч, поэтому «125,5»
/// разберётся как 1255. В сортировке это косметика, в сверке — неверная находка, то есть ровно та
/// ошибка, ради предотвращения которой подсистема и делается.
///
/// Принятое допущение: данные русские. Пробелы (включая неразрывный) — разделители тысяч, запятая —
/// десятичный разделитель. Когда встречаются обе — десятичным считается ПОСЛЕДНИЙ разделитель, что
/// одинаково верно и для «1 234,5», и для «1,234.5».
/// </summary>
public static class QuantityParser
{
    /// <summary>Ведущее число; хвост вроде «м», «шт.» отбрасывается — единицы в этих таблицах пишут
    /// в той же ячейке, и требовать чистого числа значило бы терять половину строк.</summary>
    private static readonly Regex Leading = new(@"^[-+]?[0-9][0-9.,]*", RegexOptions.Compiled);

    public static bool TryParse(string? raw, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var cleaned = new string([.. raw.Where(c => !char.IsWhiteSpace(c) && c != ' ')]);
        var m = Leading.Match(cleaned);
        if (!m.Success) return false;

        var token = m.Value;
        var lastSep = Math.Max(token.LastIndexOf(','), token.LastIndexOf('.'));

        string normalized;
        if (lastSep < 0)
        {
            normalized = token;
        }
        else
        {
            // Всё до последнего разделителя — разряды, сам последний — десятичная точка.
            var head = token[..lastSep].Replace(",", "").Replace(".", "");
            var tail = token[(lastSep + 1)..];
            normalized = tail.Length == 0 ? head : $"{head}.{tail}";
        }

        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Число либо <c>null</c> — отличие «нет значения» от «ноль» существенно: ноль это
    /// заявленное количество, отсутствие — незаполненная ячейка.</summary>
    public static double? Parse(string? raw) => TryParse(raw, out var v) ? v : null;
}
