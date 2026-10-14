using System.Globalization;
using BHS.CRG.Application.Tables;
using BHS.CRG.Infrastructure.DataSets;

namespace BHS.CRG.Api.Modules.Tables;

/// <summary>
/// Отбор таблицы словами — для шапки выгруженного файла (ТЗ CORE-33; задача G3b, issue #1096):
/// «Поставщик: «Электросила» и Сумма > 100000».
///
/// <para><b>Слова собирает сервер, а не берёт у экрана.</b> Файл уходит из системы и живёт своей
/// жизнью; строка «что отобрано» в нём — утверждение о строках ниже. Возьми мы её у клиента, шапка
/// могла бы сказать одно, а строки — другое: отбор исполняется деревом, а подпись приехала бы
/// отдельным полем, и ничто не сверяло бы их между собой. Здесь слова и строки — из одного дерева.</para>
///
/// <para>Подписи колонок — из ВСЕХ колонок таблицы, а не из выгруженных: отбор может стоять на
/// колонке, которой в файле нет, и тогда в шапке она обязана быть названа своим именем.</para>
/// </summary>
public static class TableFilterWords
{
    /// <summary>Отбор словами; null — отбора нет (пустое дерево ничего не ограничивает).</summary>
    public static string? Describe(FilterNode? root, IReadOnlyList<TableColumnDto> columns)
    {
        if (root is null) return null;
        var byKey = columns.ToDictionary(c => c.Key, StringComparer.Ordinal);
        return Node(root, byKey, top: true);
    }

    private static string? Node(FilterNode node, Dictionary<string, TableColumnDto> columns, bool top)
    {
        if (node.Type != "group") return Condition(node, columns);

        var parts = (node.Children ?? []).Select(c => Node(c, columns, top: false)).OfType<string>().ToList();
        if (parts.Count == 0) return null;
        if (parts.Count == 1) return parts[0];

        var joined = string.Join(node.Logic == "or" ? " или " : " и ", parts);
        // Скобки — у вложенной группы: «A и (B или C)» без них читалось бы как «(A и B) или C».
        return top ? joined : $"({joined})";
    }

    private static string Condition(FilterNode node, Dictionary<string, TableColumnDto> columns)
    {
        var column = columns.GetValueOrDefault(node.Column ?? "");
        var name = column?.Label ?? node.Column ?? "?";
        var op = node.Op ?? "eq";
        var values = DataSetRowFilterExecutor.ValuesOf(node).Select(v => Value(v, column?.Kind)).ToList();

        if (Presence.TryGetValue(op, out var presence)) return $"{name}: {presence}";
        return op switch
        {
            "eq" => $"{name}: {values.FirstOrDefault()}",
            "between" => $"{name}: от {values.ElementAtOrDefault(0)} до {values.ElementAtOrDefault(1)}",
            "in" => $"{name}: {string.Join(", ", values)}",
            "not_in" => $"{name} — кроме: {string.Join(", ", values)}",
            // Оператор, которого здесь не знают, назван кодом, а не пропущен: пропуск сделал бы отбор в
            // шапке шире, чем он есть.
            _ => $"{name} {Words.GetValueOrDefault(op, op)} {values.FirstOrDefault()}",
        };
    }

    /// <summary>Значение так, как его читает человек: дата — днём, флаг — «да / нет», текст — в кавычках.</summary>
    private static string Value(string value, string? kind) => kind switch
    {
        TableOperators.Date when DateOnly.TryParseExact(
            value.Length >= 10 ? value[..10] : value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var day) => day.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
        TableOperators.Boolean => value == "true" ? "да" : value == "false" ? "нет" : $"«{value}»",
        TableOperators.Number => value,
        _ => $"«{value}»",
    };

    private static readonly Dictionary<string, string> Presence = new(StringComparer.Ordinal)
    {
        ["is_empty"] = "пусто", ["is_not_empty"] = "не пусто", ["is_null"] = "не определено", ["is_not_null"] = "определено",
    };

    private static readonly Dictionary<string, string> Words = new(StringComparer.Ordinal)
    {
        ["neq"] = "≠", ["gt"] = ">", ["gte"] = "≥", ["lt"] = "<", ["lte"] = "≤",
        ["contains"] = "содержит", ["not_contains"] = "не содержит",
        ["starts_with"] = "начинается с", ["ends_with"] = "заканчивается на",
    };
}
