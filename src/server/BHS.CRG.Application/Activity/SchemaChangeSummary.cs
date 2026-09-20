using System.Text.Json;

namespace BHS.CRG.Application.Activity;

/// <summary>
/// Что изменилось в схеме типа — одной строкой для журнала действий (ТЗ CORE-28).
///
/// Схему целиком журнал не хранит нарочно: это десятки килобайт JSON на каждую правку, и читать
/// такую запись человек не станет. Вопрос, который задают журналу, звучит иначе — «куда делось
/// поле» и «когда его сделали обязательным», — и на него отвечает перечень ключей.
///
/// ⚠️ Разбор намеренно поверхностный: состав ключей верхнего уровня и признак «объявление поля
/// изменилось». Вглубь — в тип, тэги, варианты перечисления — не лезем: описать такое изменение
/// строкой всё равно не выйдет, а притворяться, что выйдет, хуже, чем сказать «изменено».
/// </summary>
public static class SchemaChangeSummary
{
    /// <summary>Перечень собственных полей схемы: то, что уйдёт в журнал как «было».</summary>
    public static string Describe(JsonDocument schema)
    {
        var keys = Fields(schema).Keys.Order(StringComparer.Ordinal).ToList();
        return keys.Count == 0 ? "полей нет" : "поля: " + string.Join(", ", keys);
    }

    /// <summary>Что сделали: добавлено, убрано, изменено. Пусто не бывает — «ничего» тоже ответ.</summary>
    public static string Describe(JsonDocument before, JsonDocument after)
    {
        var was = Fields(before);
        var now = Fields(after);

        var added = now.Keys.Except(was.Keys).Order(StringComparer.Ordinal).ToList();
        var removed = was.Keys.Except(now.Keys).Order(StringComparer.Ordinal).ToList();
        var changed = now.Keys.Intersect(was.Keys)
            .Where(k => was[k] != now[k])
            .Order(StringComparer.Ordinal).ToList();

        var parts = new List<string>();
        if (added.Count > 0) parts.Add("добавлено: " + string.Join(", ", added));
        if (removed.Count > 0) parts.Add("убрано: " + string.Join(", ", removed));
        if (changed.Count > 0) parts.Add("изменено: " + string.Join(", ", changed));

        // Схему сохранили, а поля прежние: поправили что-то вне их состава — порядок, группы,
        // печатные блоки. Записать «нет изменений» честнее, чем промолчать: сам факт сохранения
        // схемы остаётся событием, у которого есть автор и время.
        return parts.Count == 0 ? "состав полей не изменился" : string.Join("; ", parts);
    }

    /// <summary>
    /// Ключ поля → текст его объявления (для сравнения «изменилось ли»).
    ///
    /// Сравнение текстом, а не по значениям: перестановка свойств внутри объявления покажется
    /// изменением. Ошибка выбрана осознанно в эту сторону — лишняя строка в журнале заметна и
    /// безобидна, пропущенная правка схемы не заметна ничем.
    /// </summary>
    private static Dictionary<string, string> Fields(JsonDocument schema)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var root = schema.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return result;
        if (!root.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var f in fields.EnumerateArray())
        {
            if (f.ValueKind != JsonValueKind.Object) continue;
            if (!f.TryGetProperty("key", out var k) || k.ValueKind != JsonValueKind.String) continue;
            var key = k.GetString();
            if (string.IsNullOrEmpty(key)) continue;
            result[key] = f.GetRawText();
        }
        return result;
    }
}
