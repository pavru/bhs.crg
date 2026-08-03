using System.Globalization;
using System.Text.Json.Nodes;
using BHS.CRG.Domain.Catalog;

namespace BHS.CRG.Application.Schema;

/// <summary>
/// Приведение хранимого значения к объявленному типу поля — исправление аудита «привести» (issue #643).
///
/// Это «узкое B» из #641: приведение живёт в аудите и срабатывает по явной команде человека, а не на
/// каждой записи. На пути записи его нет намеренно — иначе значение молча менялось бы в пяти разных
/// местах (форма, распознавание, вставка, авто-маппер, привязка набора), и понять, кто его переписал,
/// стало бы невозможно.
///
/// Разбор здесь СТРОГИЙ и этим отличается от <c>DataSetValueCoercion</c>/<c>QuantityParser</c>, где
/// «10 м» законно читается как 10: в ячейке набора единица измерения — обычное дело, а здесь мы
/// правим уже сохранённое значение, и выбросить из него «м» значит потерять то, что человек написал.
/// Не разобралось целиком — не приводим и говорим почему.
/// </summary>
public static class ValueCoercion
{
    /// <summary>
    /// Кнопка «Привести» стоит у ЛЮБОЙ находки о значении, а тонкий слой ловит и нарушения
    /// ограничений типа — шаблон, длину, границы. Там значение уже нужного вида, и приведение
    /// бессильно; сказать в ответ «значение уже нужного вида» значило бы возразить самой находке,
    /// на которую человек и нажал.
    /// </summary>
    private const string NotACoercionReason =
        "Значение нужного вида — расходится не вид, а ограничение типа (формат, длина, границы); приведением это не чинится.";

    /// <summary>
    /// Приведённое значение для поля. <c>false</c> + <paramref name="reason"/>, если приводить нечего
    /// или не к чему: значение уже нужного вида, тип поля не скалярный, разбор не удался.
    /// </summary>
    public static bool TryCoerce(
        SchemaFieldInfo field, JsonNode? value,
        IReadOnlyDictionary<Guid, PrimitiveType> primitivesById,
        out JsonNode? coerced, out string? reason)
    {
        coerced = null; reason = null;

        if (value is null) { reason = "Значение отсутствует."; return false; }
        if (value is JsonObject or JsonArray)
        {
            // Составное значение в скалярном поле — не приведение, а разбор содержимого: что из
            // объекта считать значением, знает только человек.
            reason = "Составное значение привести нельзя — исправьте вручную.";
            return false;
        }

        var (baseType, primitive) = BaseTypeOf(field, primitivesById);
        if (baseType is null) { reason = "Для этого типа поля приведение не определено."; return false; }

        var element = value.GetValueKind();
        var raw = RawText(value, element);

        switch (baseType)
        {
            case "number":
            {
                if (element == System.Text.Json.JsonValueKind.Number)
                {
                    // Число уже число: единственное, чем оно бывает расхождением, — дробь в целом
                    // типе, а её приведением не чинят (см. IsIntegerOnly ниже).
                    reason = IsIntegerOnly(primitive) && !IsWhole(value.GetValue<double>())
                        ? FractionReason(value.GetValue<double>())
                        : NotACoercionReason;
                    return false;
                }
                if (!TryParseNumber(raw, out var n))
                {
                    reason = $"«{raw}» не разбирается как число.";
                    return false;
                }
                if (IsIntegerOnly(primitive) && !IsWhole(n)) { reason = FractionReason(n); return false; }
                coerced = NumberNode(n);
                return true;
            }

            case "bool" or "boolean":
            {
                if (element is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                { reason = NotACoercionReason; return false; }
                if (TryParseBool(raw, out var b)) { coerced = JsonValue.Create(b); return true; }
                reason = $"«{raw}» не разбирается как логическое значение.";
                return false;
            }

            case "date":
            {
                if (raw is null) { reason = "Значение отсутствует."; return false; }
                // Дата хранится строкой ISO. Русский формат «01.02.2026» — то, что приходит от
                // распознавания и из ячеек; инвариантная культура прочла бы его как 2 января.
                if (!DateTime.TryParse(raw, CultureInfo.GetCultureInfo("ru-RU"), DateTimeStyles.None, out var d)
                    && !DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
                {
                    reason = $"«{raw}» не разбирается как дата.";
                    return false;
                }
                var iso = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (iso == raw) { reason = NotACoercionReason; return false; }
                coerced = JsonValue.Create(iso);
                return true;
            }

            case "enum":
                // Приведением получился бы КОД, которого нет ни в одном варианте перечисления:
                // находка исчезла бы, а значение осталось бы нерабочим — худший исход, потому что
                // невидимый. Вариант выбирает человек.
                reason = "Вариант перечисления выбирается вручную — приведением его не угадать.";
                return false;

            default: // string / text
            {
                if (element == System.Text.Json.JsonValueKind.String)
                { reason = NotACoercionReason; return false; }
                coerced = JsonValue.Create(raw ?? "");
                return true;
            }
        }
    }

    /// <summary>Базовый тип поля и примитив, если поле объявлено примитивом. null — поле не скалярное.</summary>
    private static (string? BaseType, PrimitiveType? Primitive) BaseTypeOf(
        SchemaFieldInfo field, IReadOnlyDictionary<Guid, PrimitiveType> primitivesById) => field.Type switch
    {
        "primitive" => field.TypeId is { } id && primitivesById.TryGetValue(id, out var p)
            ? (p.BaseType, p)
            : (null, null),
        "number" or "date" or "bool" or "boolean" or "string" or "text" or "enum" => (field.Type, null),
        _ => (null, null),
    };

    private static bool IsIntegerOnly(PrimitiveType? primitive)
        => primitive?.Constraints.RootElement is { } c
           && c.ValueKind == System.Text.Json.JsonValueKind.Object
           && c.TryGetProperty("integer", out var f)
           && f.ValueKind == System.Text.Json.JsonValueKind.True;

    private static bool IsWhole(double n) => Math.Abs(n - Math.Truncate(n)) < double.Epsilon;

    /// <summary>
    /// Целочисленность НЕ чиним: «2.1» в поле «Цело число» — это не опечатка формата, а либо
    /// иерархическая нумерация (ровно случай #461), либо настоящая дробь. И округление, и отбрасывание
    /// дробной части выдумали бы данные, которых никто не вводил; правильное решение здесь — либо
    /// поправить объявление типа, либо переписать значение руками.
    /// </summary>
    private static string FractionReason(double n)
        => $"{n.ToString(CultureInfo.InvariantCulture)} — дробное, а тип допускает только целые; округление придумало бы данные.";

    /// <summary>Целое пишем целым: «1.0» в JSON выглядит опечаткой там, где счёт идёт штуками.</summary>
    private static JsonNode NumberNode(double n)
        => Math.Abs(n - Math.Truncate(n)) < double.Epsilon && Math.Abs(n) < 9.2e18
            ? JsonValue.Create((long)n)
            : JsonValue.Create(n);

    /// <summary>
    /// Число целиком, без хвоста. Пробелы (в том числе неразрывный) — разряды, запятая — десятичный
    /// разделитель; когда встречаются оба, десятичным считается последний, что верно и для «1 234,5»,
    /// и для «1,234.5» (то же допущение, что в <c>QuantityParser</c>).
    ///
    /// Разделителей больше одного — только если предыдущие делят разряды ПО ТРИ цифры. Без этой
    /// проверки «2.1.3» превращалось в 21.3, а «01.02.2026» — в 102.2026: приведение выдумывало
    /// число из иерархической нумерации и из даты, отвечая при этом «Документ исправлен». Ровно тот
    /// исход, ради недопущения которого целочисленность и не округляется.
    /// </summary>
    private static bool TryParseNumber(string? raw, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var cleaned = new string([.. raw.Where(ch => !char.IsWhiteSpace(ch) && ch != ' ')]);
        var lastSep = Math.Max(cleaned.LastIndexOf(','), cleaned.LastIndexOf('.'));
        if (lastSep < 0)
            return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        var head = cleaned[..lastSep];
        if (!IsGrouped(head)) return false;

        var normalized = $"{head.Replace(",", "").Replace(".", "")}.{cleaned[(lastSep + 1)..]}";
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Целая часть: либо без разделителей вовсе, либо разряды по три одним и тем же знаком.</summary>
    private static bool IsGrouped(string head)
    {
        // Мешать знаки нельзя: «1,234.567» — не запись числа, а следы двух разных соглашений.
        if (head.Contains(',') && head.Contains('.')) return false;
        var groups = head.Split(',', '.');
        if (groups.Length == 1) return true;
        if (groups[0].Length is 0 or > 3) return false;
        return groups.Skip(1).All(g => g.Length == 3);
    }

    /// <summary>
    /// Текст значения. Своими руками, а не <c>ToString()</c>: у <c>JsonElement</c> тот отдаёт для
    /// логического значения «True» с большой буквы (<c>bool.TrueString</c>), и приведение к строке
    /// клало в русский документ «True» вместо «true».
    /// </summary>
    private static string? RawText(JsonNode value, System.Text.Json.JsonValueKind kind) => kind switch
    {
        System.Text.Json.JsonValueKind.String => value.GetValue<string>(),
        System.Text.Json.JsonValueKind.True => "true",
        System.Text.Json.JsonValueKind.False => "false",
        _ => value.ToJsonString(),
    };

    /// <summary>Русские «да»/«нет» — то, что кладёт распознавание: в скане стоит слово, не флажок.</summary>
    private static bool TryParseBool(string? raw, out bool value)
    {
        value = false;
        var s = raw?.Trim().ToLowerInvariant();
        switch (s)
        {
            case "да" or "true" or "1" or "истина" or "yes": value = true; return true;
            case "нет" or "false" or "0" or "ложь" or "no": return true;
            default: return false;
        }
    }
}
