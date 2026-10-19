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
        // Косая черта — литералом: без кавычек «/» в формате означает разделитель дат КУЛЬТУРЫ, у
        // русской это точка, и «12/03/2026» не читалось бы никогда.
        "d.M.yyyy", "dd.MM.yyyy", "yyyy-MM-dd", "d'/'M'/'yyyy", "d MMMM yyyy", "d MMMM yyyy 'г.'", "d MMMM yyyy 'г'",
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
    /// Ставка НДС в процентах (issue #1410): «22%», «22 %», «НДС 22», «10,0» → число; «Без НДС» → 0.
    /// Только от 0 до 100 — как принимает правка строки; список ставок не зашит: основная с 2026 года —
    /// 22 %, и следующая перемена ставок обязана проходить без правки здесь. <c>null</c> — не читается
    /// однозначно: расчётная «20/120», ставка вне границ, слова.
    /// </summary>
    public static decimal? VatRate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (NoVat(text)) return 0m;

        var bare = RateWords().Replace(text, " ").Trim();
        return Number(bare, maxFraction: 2) is { } rate && rate is >= 0 and <= 100 ? rate : null;
    }

    /// <summary>
    /// «Без НДС», «НДС не облагается» — НДС назван, и назван нулём. Это не пустая ячейка: строка такая
    /// говорит, что налога нет, а не молчит о нём.
    /// </summary>
    public static bool NoVat(string? text) => text is not null && NoVatWords().IsMatch(text);

    /// <summary>
    /// Количество. Тут разделитель один и он дробный: «0,125» м или «1,5» шт — обычное дело, а тысячи
    /// в количестве разделителем не пишут. Два разных разделителя — как у суммы. Знаков после
    /// разделителя — не больше трёх: точнее тысячной количество счёт не хранит.
    ///
    /// <para>⚠️ Цену этим разбором не читают — только <see cref="Money" />: «1.250» в графе цены бывает
    /// тысячей двести пятьюдесятью, и прочитанное дробью дало бы цену в тысячу раз меньше.</para>
    /// </summary>
    public static decimal? Quantity(string? text) => Number(text, maxFraction: 3);

    /// <summary>
    /// Число в поле, которое заказчик дописал в тип счёта (issue #1483): что это за число, разбор не знает —
    /// деньги, проценты или метры. Дробная часть любой длины, но «1.250» и «1,250» — НЕ число: три цифры
    /// после единственного разделителя бывают и тысячами, и дробью, и угадать значит записать в тысячу раз
    /// не то. Такое уходит в предложение, человек впишет сам. Разбор ядра (<c>ValueCoercion</c>) здесь не
    /// годится нарочно: «1.250» он читает как 1,25.
    /// </summary>
    public static decimal? Decimal(string? text) => Number(text, maxFraction: 10, threeDigitsAmbiguous: true);

    private static decimal? Number(string? text, int maxFraction, bool threeDigitsAmbiguous = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Валюта и единицы в хвосте — «руб.», «₽», «р.» — и пробелы внутри числа, включая неразрывные.
        var cleaned = Tail().Replace(text.Trim(), string.Empty);
        // Тысячи уже отделены пробелом («1 234,567») — разделитель после них дробный, гадать не о чем (ревью
        // PR #1523). Смотреть надо ДО того, как пробелы уйдут: после «1234,567» неотличимо от «1,234».
        var grouped = SpaceGroups().IsMatch(cleaned);
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
            // «0,125» — дробь при любом прочтении: тысяч с нулём впереди не пишут.
            if (threeDigitsAmbiguous && fraction.Length == 3 && !grouped && !whole.Contains(groupMark)
                && whole.TrimStart('-') != "0")
                return null;

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

    [GeneratedRegex(@"\d[\s  ]+\d{3}(?!\d)")]
    private static partial Regex SpaceGroups();

    [GeneratedRegex(@"\s*(руб(лей|ля|\.)?|р\.|₽|RUB)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Tail();

    [GeneratedRegex(@"^-?\d[\d.,]*$")]
    private static partial Regex Shape();

    [GeneratedRegex(@"^\s*(без\s+ндс|ндс\s+не\s+облагается|не\s+облагается(\s+ндс)?)\.?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex NoVatWords();

    [GeneratedRegex(@"ставка|ндс|%", RegexOptions.IgnoreCase)]
    private static partial Regex RateWords();
}
