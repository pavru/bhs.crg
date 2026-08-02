using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BHS.CRG.Domain.Catalog;

namespace BHS.CRG.Application.Schema;

/// <summary>
/// Правила «скалярное значение соответствует объявленному типу поля» — ОДНА реализация на все пути
/// проверки (issue #642, вариант C из #641).
///
/// До этого правил было две штуки разной полноты: <see cref="Generation.ValueTypeScanner"/> знал про
/// базовый тип, шаблон примитива, целочисленность и границы, но запускался только в пайплайне
/// генерации документа; <see cref="SchemaDataAuditor"/> обходил ВСЕ объекты типа (включая записи
/// общих данных), но сравнивал лишь вид значения JSON. На живой базе это значило, что из 54 объектов
/// тонкий слой видел 10, а 44 записи общих данных не проверял никто.
///
/// Здесь — только ЛИСТ (скаляр). Форму контейнера (массив пришёл вместо объекта и т.п.) каждый
/// вызывающий проверяет сам: сканер идёт по разрешённому контексту, аудитор — по сырым данным, и
/// обходы у них разные. Сведи их сюда — и аудит выдал бы на одно расхождение две находки разными
/// словами.
///
/// Всё — предупреждениями: данные накоплены, и запрет остановил бы выпуск половины документов в тот
/// же день. Что чинить, объявление типа или значение, решает человек.
/// </summary>
public static class ValueTypeRules
{
    private static readonly IReadOnlyList<string> None = [];

    /// <summary>
    /// Расхождения значения с типом поля, готовыми сообщениями. Пустой список — расхождений нет.
    /// Составные поля, ссылки, файлы и картинки не наши: для них всегда пусто.
    /// </summary>
    public static IReadOnlyList<string> CheckScalar(
        SchemaFieldInfo field, JsonElement value, IReadOnlyDictionary<Guid, PrimitiveType> primitivesById)
    {
        // Расчётное поле производное: претензия к его значению — претензия к выражению (#368).
        if (field.Computed) return None;
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return None;

        switch (field.Type)
        {
            case "array" or "complex" or "doc-ref" or "doc-array" or "file" or "image":
                return None;

            case "primitive":
            {
                if (field.TypeId is not { } id || !primitivesById.TryGetValue(id, out var primitive)) return None;
                var messages = new List<string>();
                // Базовый тип не сошёлся — ограничения проверять незачем: они говорили бы о значении,
                // которого в этом поле вообще быть не может.
                if (CheckBase(field, value, primitive.BaseType, primitive.Name) is { } baseMessage)
                    messages.Add(baseMessage);
                else
                    CheckConstraints(field, value, primitive, messages);
                return messages;
            }

            default:
                return CheckBase(field, value, field.Type) is { } m ? new[] { m } : None;
        }
    }

    /// <summary>Базовый тип. Сообщение о расхождении либо null, если сошлось.</summary>
    private static string? CheckBase(SchemaFieldInfo field, JsonElement value, string baseType, string? typeName = null)
    {
        var ok = baseType switch
        {
            "number" => value.ValueKind == JsonValueKind.Number,
            // "bool" — базовый тип примитива, "boolean" — тип поля схемы; одно и то же по смыслу.
            // Второе имя добавлено вместе с вынесением правил: до этого поля-флажки проваливались в
            // «неизвестный вид» и не проверялись вовсе, хотя распознавание кладёт в них «да»/«нет».
            "bool" or "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            // Дата хранится строкой ISO — отдельная проверка разбора ниже, в ограничениях.
            "string" or "date" or "enum" or "text" => value.ValueKind == JsonValueKind.String,
            _ => true, // неизвестный вид — молчим, лучше промолчать, чем выдумать претензию
        };
        if (ok) return null;

        var actual = value.ValueKind switch
        {
            JsonValueKind.String => "строка",
            JsonValueKind.Number => "число",
            JsonValueKind.True or JsonValueKind.False => "логическое значение",
            JsonValueKind.Array => "таблица",
            JsonValueKind.Object => "составное значение",
            _ => "пусто",
        };
        var expected = typeName is null ? Expected(baseType) : $"{typeName} ({Expected(baseType)})";
        return $"Поле «{Title(field)}»: ожидается {expected}, а хранится {actual}.";
    }

    private static void CheckConstraints(
        SchemaFieldInfo field, JsonElement value, PrimitiveType primitive, List<string> messages)
    {
        var c = primitive.Constraints.RootElement;
        if (c.ValueKind != JsonValueKind.Object) return;

        double? Num(string name) => c.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : null;
        string? Str(string name) => c.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
        var flag = c.TryGetProperty("integer", out var f) && f.ValueKind == JsonValueKind.True;

        if (primitive.BaseType == "number" && value.TryGetDouble(out var n))
        {
            if (flag && Math.Abs(n - Math.Truncate(n)) > double.Epsilon)
                messages.Add($"Поле «{Title(field)}»: тип «{primitive.Name}» допускает только целые, а хранится {n.ToString(CultureInfo.InvariantCulture)}.");
            if (Num("min") is { } min && n < min)
                messages.Add($"Поле «{Title(field)}»: значение меньше допустимого ({min}).");
            if (Num("max") is { } max && n > max)
                messages.Add($"Поле «{Title(field)}»: значение больше допустимого ({max}).");
        }

        if (primitive.BaseType == "string" && value.GetString() is { } s)
        {
            if (Num("minLength") is { } minLen && s.Length < minLen)
                messages.Add($"Поле «{Title(field)}»: короче допустимого ({minLen} симв.).");
            if (Num("maxLength") is { } maxLen && s.Length > maxLen)
                messages.Add($"Поле «{Title(field)}»: длиннее допустимого ({maxLen} симв.).");
            if (Str("pattern") is { } pattern && !Matches(s, pattern))
                messages.Add(Str("patternMessage") is { } msg && msg.Length > 0
                    ? $"Поле «{Title(field)}»: {msg}"
                    : $"Поле «{Title(field)}» не соответствует формату типа «{primitive.Name}».");
        }

        if (primitive.BaseType == "date" && value.GetString() is { } d && !DateTimeOffset.TryParse(
                d, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
            messages.Add($"Поле «{Title(field)}»: значение не разбирается как дата.");
    }

    /// <summary>Битый шаблон — ошибка НАСТРОЙКИ типа, а не данных: молча пропускаем значение, чтобы
    /// не превратить одну опечатку администратора в предупреждение на каждой строке.</summary>
    private static bool Matches(string value, string pattern)
    {
        try { return Regex.IsMatch(value, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(200)); }
        catch (ArgumentException) { return true; }
        catch (RegexMatchTimeoutException) { return true; }
    }

    private static string Expected(string baseType) => baseType switch
    {
        "number" => "число",
        "date" => "дата",
        "bool" or "boolean" => "логическое значение",
        _ => "строка",
    };

    private static string Title(SchemaFieldInfo f) => f.Title ?? f.Key;
}
