using System.Globalization;
using System.Text.RegularExpressions;

namespace BHS.CRG.Application.Tables;

/// <summary>
/// Что значит условие отбора у колонки с ОБЪЯВЛЕННЫМ видом (ТЗ CORE-33; задача G1c, issue #1090).
///
/// <para><b>Сравнение идёт по виду колонки, а не по догадке.</b> У файловых наборов вида нет, и
/// исполнитель угадывает: оба значения похожи на числа — сравнивает числами, иначе строками. Для
/// таблицы модуля догадка неверна дважды: «Сумма = 110» не нашла бы 110.00 (строки разные), а «срок
/// меньше 5 мая» вернул бы счета вовсе без срока (пустая строка меньше любой). Здесь число
/// сравнивается числом, дата — датой, а клетка, в которой лежит не то, под сравнение не попадает.</para>
///
/// <para><b>Это — исполнитель в памяти; второй — запрос к базе</b> (<c>TableSql</c> в контрактах
/// модулей). Общий код у них невозможен: один считает на строках, другой пишет SQL. Что они согласны,
/// сторожит парный тест: один отбор на одних данных обязан вернуть одни и те же строки. Правило,
/// добавленное сюда и забытое там, роняет его.</para>
/// </summary>
public static partial class TableConditions
{
    /// <summary>
    /// Число — в записи, которую одинаково читают оба исполнителя: цифры, точка, знак. Запись с
    /// порядком («1e3») и с запятой числом не считается: в базе такую клетку пришлось бы разбирать
    /// иначе, чем здесь, и исполнители разошлись бы на ней молча.
    /// </summary>
    public const string NumberPattern = @"^-?[0-9]+(\.[0-9]+)?$";

    /// <summary>Дата ISO в начале значения: «2026-05-01» и «2026-05-01T00:00:00» — одна и та же дата.</summary>
    public const string DatePattern = @"^[0-9]{4}-[0-9]{2}-[0-9]{2}";

    /// <summary>
    /// Чем разделены элементы перечня в клетке набора данных (значения наборов — строки). Перевод
    /// строки, а не запятая: в названии стройки запятая — обычное дело («Комарова 36, 4 эт.»), а
    /// перевода строки в однострочном названии не бывает.
    /// </summary>
    public const char ListSeparator = '\n';

    /// <summary>Клетка перечня из его элементов — тем же разделителем, каким её разбирает условие.</summary>
    public static string? Join(IEnumerable<string> items) =>
        string.Join(ListSeparator, items) is { Length: > 0 } cell ? cell : null;

    /// <summary>Элементы перечня из клетки.</summary>
    public static string[] Items(string? cell) =>
        (cell ?? "").Split(ListSeparator, StringSplitOptions.RemoveEmptyEntries);

    [GeneratedRegex(NumberPattern)] private static partial Regex NumberRegex();
    [GeneratedRegex(DatePattern)] private static partial Regex DateRegex();

    /// <summary>
    /// Что не так с условием; null — годно. Проверяется ДО первой строки: условие, которое нельзя
    /// выполнить, отказывает, а не возвращает «ничего не нашлось».
    /// </summary>
    public static string? Problem(string kind, string op, IReadOnlyList<string> values)
    {
        if (!TableOperators.All.Contains(op)) return $"оператора «{op}» нет";
        if (!TableOperators.IsPresence(op) && !TableOperators.For(kind).Contains(op))
            return $"оператор «{op}» к колонке вида «{kind}» не применяется";
        if (TableOperators.ArityProblem(op, values) is { } arity) return arity;

        foreach (var value in values)
        {
            if (kind == TableOperators.Number && Number(value) is null)
                return $"значение «{value}» — не число";
            if (kind == TableOperators.Date && !DateOnly.TryParseExact(
                    value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                return $"значение «{value}» — не дата вида ГГГГ-ММ-ДД";
            if (kind == TableOperators.Boolean && value is not ("true" or "false"))
                return $"значение «{value}» — не «true» и не «false»";
        }
        return null;
    }

    /// <summary>Подходит ли клетка под условие. Условие обязано быть годным (<see cref="Problem" />).</summary>
    public static bool Matches(string kind, string op, IReadOnlyList<string> values, string? cell) =>
        Compile(kind, op, values)(cell);

    /// <summary>
    /// Условие, готовое к строкам: значения разобраны ОДИН раз. Набор данных читает таблицу целиком, и
    /// разбирать «110» заново на каждой из десятков тысяч строк незачем. Условие обязано быть годным.
    /// </summary>
    public static Func<string?, bool> Compile(string kind, string op, IReadOnlyList<string> values)
    {
        switch (op)
        {
            case "is_empty" or "is_null": return string.IsNullOrEmpty;
            case "is_not_empty" or "is_not_null": return cell => !string.IsNullOrEmpty(cell);
            // Отрицания — именно «не подошло под положительное»: клетка без значения или с мусором
            // «не равна 5» и «не входит в список». Иначе «равно» и «не равно» вместе не давали бы всех строк.
            case "neq": return Not(Compile(kind, "eq", values));
            case "not_in": return Not(Compile(kind, "in", values));
            case "not_contains": return Not(Compile(kind, "contains", values));
            case "in":
                var any = values.Select(v => Compile(kind, "eq", [v])).ToArray();
                return cell => any.Any(test => test(cell));
        }

        switch (kind)
        {
            case TableOperators.Number:
                var numbers = values.Select(v => Number(v)!.Value).ToArray();
                return cell => Number(cell) is { } n && Compare(op, i => n.CompareTo(numbers[i]));
            case TableOperators.Date:
                return cell => Date(cell) is { } d && Compare(op, i => string.CompareOrdinal(d, values[i]));
            case TableOperators.List:
                // Условие по дочернему зерну: «есть в перечне такой». Отрицания выше уже перевёрнуты
                // целиком — «нет НИ ОДНОГО такого», а не «есть хоть один другой».
                var wanted = Upper(values[0]);
                return cell => Items(cell).Any(item => TextMatches(op, Upper(item), wanted));
            default:
                var value = Upper(values[0]);
                return cell => TextMatches(op, Upper(cell), value);
        }
    }

    private static Func<string?, bool> Not(Func<string?, bool> test) => cell => !test(cell);

    /// <summary>Сравнения порядка и равенство — через одно сравнение клетки со значением условия по его номеру.</summary>
    private static bool Compare(string op, Func<int, int> cellVersus) => op switch
    {
        "eq" => cellVersus(0) == 0,
        "gt" => cellVersus(0) > 0,
        "gte" => cellVersus(0) >= 0,
        "lt" => cellVersus(0) < 0,
        "lte" => cellVersus(0) <= 0,
        "between" => cellVersus(0) >= 0 && cellVersus(1) <= 0,
        _ => false,
    };

    private static bool TextMatches(string op, string cell, string value) => op switch
    {
        "eq" => string.Equals(cell, value, StringComparison.Ordinal),
        "contains" => cell.Contains(value, StringComparison.Ordinal),
        "starts_with" => cell.StartsWith(value, StringComparison.Ordinal),
        "ends_with" => cell.EndsWith(value, StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>Регистр снимается ОДНИМ способом у клетки и у значения — тем же, что <c>upper()</c> в базе.</summary>
    public static string Upper(string? text) => (text ?? "").ToUpperInvariant();

    /// <summary>Число клетки или значения условия; null — там лежит не число.</summary>
    public static decimal? Number(string? text) =>
        text is not null && NumberRegex().IsMatch(text)
        && decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    /// <summary>Дата клетки — первые десять знаков ISO; null — там лежит не дата.</summary>
    public static string? Date(string? text) =>
        text is not null && DateRegex().IsMatch(text) ? text[..10] : null;
}
