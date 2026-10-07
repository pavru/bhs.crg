using System.Globalization;
using System.Text.RegularExpressions;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>
/// Текст из скана → значение поля счёта (issue #1077).
///
/// <para><b>Строго, и это главное.</b> Модель возвращает суммы и даты так, как они напечатаны:
/// «1 234,56 руб.», «12 марта 2026 г.», «1.234,56». Разбор, который «что-нибудь да вернёт», здесь
/// опасен: «1.234» — это тысяча двести тридцать четыре или один и двести тридцать четыре тысячных?
/// Угадать нельзя, а неверно угаданная сумма счёта выглядит ровно как верная. Поэтому всё, что не
/// читается однозначно, — <c>null</c>: поле остаётся пустым, а текст из скана показывается рядом,
/// и человек вписывает число сам.</para>
/// </summary>
public static partial class RecognizedValues
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    private static readonly string[] DateFormats =
    [
        "d.M.yyyy", "dd.MM.yyyy", "yyyy-MM-dd", "d/M/yyyy", "d MMMM yyyy", "d MMMM yyyy 'г.'", "d MMMM yyyy 'г'",
        "d MMMM yyyy 'года'",
    ];

    /// <summary>Дата с четырёхзначным годом. Двузначный год не читаем: «01.02.26» — это какой век?</summary>
    public static DateOnly? Date(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var cleaned = Spaces().Replace(text.Trim().Trim('«', '»', '"'), " ");
        return DateOnly.TryParseExact(cleaned, DateFormats, Russian, DateTimeStyles.None, out var date) ? date : null;
    }

    /// <summary>
    /// Сумма в рублях. Читается, когда дробная часть однозначна: одна-две цифры после последнего
    /// разделителя («1 234,56», «1234.5», «1.234,56», «1,234.56») либо разделителей нет вовсе.
    /// Три цифры после единственного разделителя («1.234») — неоднозначно, не читаем.
    /// </summary>
    public static decimal? Money(string? text) => Number(text, maxFraction: 2);

    /// <summary>
    /// Количество и цена. Тут разделитель один и он дробный: «0,125» м или «1,5» шт — обычное дело,
    /// а тысячи в количестве разделителем не пишут. Два разных разделителя — как у суммы.
    /// </summary>
    public static decimal? Quantity(string? text) => Number(text, maxFraction: 6);

    private static decimal? Number(string? text, int maxFraction)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Валюта и единицы в хвосте — «руб.», «₽», «р.» — и пробелы внутри числа, включая неразрывные.
        var cleaned = Tail().Replace(text.Trim(), string.Empty);
        cleaned = Spaces().Replace(cleaned, string.Empty);
        if (cleaned.Length == 0 || !Shape().IsMatch(cleaned)) return null;

        var comma = cleaned.LastIndexOf(',');
        var dot = cleaned.LastIndexOf('.');
        var last = Math.Max(comma, dot);

        string whole, fraction;
        if (last < 0)
        {
            (whole, fraction) = (cleaned, string.Empty);
        }
        else
        {
            (whole, fraction) = (cleaned[..last], cleaned[(last + 1)..]);
            var decimalMark = cleaned[last];
            var groupMark = decimalMark == ',' ? '.' : ',';

            // Дробный разделитель один. Второй такой же — это уже не число, которое мы берёмся читать.
            if (whole.Contains(decimalMark)) return null;
            if (fraction.Length == 0 || fraction.Length > maxFraction) return null;

            if (whole.Contains(groupMark))
            {
                // Другой разделитель слева — разряды: группы строго по три цифры, иначе не читаем.
                var groups = whole.TrimStart('-').Split(groupMark);
                if (groups[0].Length is 0 or > 3 || groups.Skip(1).Any(g => g.Length != 3)) return null;
                whole = whole.Replace(groupMark.ToString(), string.Empty);
            }
        }

        return decimal.TryParse($"{whole}.{(fraction.Length == 0 ? "0" : fraction)}",
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    [GeneratedRegex(@"[\s  ]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\s*(руб(лей|ля|\.)?|р\.|₽|RUB)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Tail();

    [GeneratedRegex(@"^-?\d[\d.,]*$")]
    private static partial Regex Shape();
}
