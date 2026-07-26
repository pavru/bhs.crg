using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Generation;

/// <summary>
/// Соответствие значений объявленному типу поля (issue #461).
///
/// Повод: поле «ПорядковыйНомер» объявлено примитивом «Цело число», а хранит иерархическую нумерацию
/// «2.1», «3.4», «10». Восемнадцать значений строки, одно — дробное число; <c>validate_document</c>
/// возвращал ноль ошибок и ноль предупреждений, потому что соответствие типу не проверялось вовсе.
///
/// Всё выдаётся ПРЕДУПРЕЖДЕНИЯМИ: данные накоплены, и половина документов перестала бы выпускаться в
/// тот же день. Задача — показать расхождение, а не заблокировать работу; что чинить — объявление
/// типа или данные — решает человек.
/// </summary>
public static class ValueTypeScanner
{
    /// <summary>Глубина обхода составных полей: защита от патологически вложенных схем.</summary>
    private const int MaxDepth = 6;

    public static void Scan(
        GenerationContext ctx,
        IReadOnlyList<SchemaFieldInfo> effectiveFields,
        IReadOnlyDictionary<Guid, DocumentType> typesById,
        IReadOnlyDictionary<Guid, PrimitiveType> primitivesById,
        List<ResolutionDiagnostic> diagnostics)
    {
        foreach (var f in effectiveFields)
        {
            if (!ctx.Data.TryGetValue(f.Key, out var raw) || raw is not JsonElement value) continue;
            Check(f.Key, f, value, typesById, primitivesById, diagnostics, depth: 0);
        }
    }

    private static void Check(
        string path, SchemaFieldInfo field, JsonElement value,
        IReadOnlyDictionary<Guid, DocumentType> typesById,
        IReadOnlyDictionary<Guid, PrimitiveType> primitivesById,
        List<ResolutionDiagnostic> diagnostics, int depth)
    {
        // Расчётное поле производное: претензия к его значению — претензия к выражению (#368).
        if (field.Computed) return;
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return;
        if (depth >= MaxDepth) return;

        switch (field.Type)
        {
            case "array":
                if (value.ValueKind != JsonValueKind.Array)
                {
                    Warn(diagnostics, path, $"Поле «{Title(field)}» — таблица, а хранится одиночное значение.");
                    return;
                }
                var i = 0;
                foreach (var item in value.EnumerateArray())
                    CheckComposite($"{path}[{i++}]", field, item, typesById, primitivesById, diagnostics, depth);
                return;

            case "complex":
                CheckComposite(path, field, value, typesById, primitivesById, diagnostics, depth);
                return;

            // Ссылки, файлы и картинки проверяет резолвер ссылок; здесь их трогать нечем.
            case "doc-ref" or "doc-array" or "file" or "image":
                return;

            case "primitive":
                CheckPrimitive(path, field, value, primitivesById, diagnostics);
                return;

            default:
                CheckBase(path, field, value, field.Type, diagnostics);
                return;
        }
    }

    private static void CheckComposite(
        string path, SchemaFieldInfo field, JsonElement value,
        IReadOnlyDictionary<Guid, DocumentType> typesById,
        IReadOnlyDictionary<Guid, PrimitiveType> primitivesById,
        List<ResolutionDiagnostic> diagnostics, int depth)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            Warn(diagnostics, path, $"Поле «{Title(field)}» — составное, а хранится простое значение.");
            return;
        }
        if (field.TypeId is not { } typeId || !typesById.ContainsKey(typeId)) return;

        // Именно здесь и живёт находка: верхнего уровня недостаточно, «ПорядковыйНомер» лежит внутри
        // элементов массива «Работы».
        foreach (var inner in DocumentTypeSchemaReader.EffectiveFields(typeId, typesById))
        {
            if (!value.TryGetProperty(inner.Key, out var innerValue)) continue;
            Check($"{path}.{inner.Key}", inner, innerValue, typesById, primitivesById, diagnostics, depth + 1);
        }
    }

    private static void CheckPrimitive(
        string path, SchemaFieldInfo field, JsonElement value,
        IReadOnlyDictionary<Guid, PrimitiveType> primitivesById, List<ResolutionDiagnostic> diagnostics)
    {
        if (field.TypeId is not { } id || !primitivesById.TryGetValue(id, out var primitive)) return;

        if (!CheckBase(path, field, value, primitive.BaseType, diagnostics, primitive.Name)) return;
        CheckConstraints(path, field, value, primitive, diagnostics);
    }

    /// <summary>Базовый тип. Возвращает false, если он уже не сошёлся — ограничения проверять незачем.</summary>
    private static bool CheckBase(
        string path, SchemaFieldInfo field, JsonElement value, string baseType,
        List<ResolutionDiagnostic> diagnostics, string? typeName = null)
    {
        var actual = value.ValueKind switch
        {
            JsonValueKind.String => "строка",
            JsonValueKind.Number => "число",
            JsonValueKind.True or JsonValueKind.False => "логическое значение",
            JsonValueKind.Array => "таблица",
            JsonValueKind.Object => "составное значение",
            _ => "пусто",
        };

        var ok = baseType switch
        {
            "number" => value.ValueKind == JsonValueKind.Number,
            "bool" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            // Дата хранится строкой ISO — отдельная проверка разбора ниже, в ограничениях.
            "string" or "date" or "enum" or "text" => value.ValueKind == JsonValueKind.String,
            _ => true, // неизвестный вид — молчим, лучше промолчать, чем выдумать претензию
        };

        if (!ok)
        {
            var expected = typeName is null ? Expected(baseType) : $"{typeName} ({Expected(baseType)})";
            Warn(diagnostics, path,
                $"Поле «{Title(field)}»: ожидается {expected}, а хранится {actual}.");
        }
        return ok;
    }

    private static void CheckConstraints(
        string path, SchemaFieldInfo field, JsonElement value, PrimitiveType primitive,
        List<ResolutionDiagnostic> diagnostics)
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
                Warn(diagnostics, path,
                    $"Поле «{Title(field)}»: тип «{primitive.Name}» допускает только целые, а хранится {n.ToString(CultureInfo.InvariantCulture)}.");
            if (Num("min") is { } min && n < min)
                Warn(diagnostics, path, $"Поле «{Title(field)}»: значение меньше допустимого ({min}).");
            if (Num("max") is { } max && n > max)
                Warn(diagnostics, path, $"Поле «{Title(field)}»: значение больше допустимого ({max}).");
        }

        if (primitive.BaseType == "string" && value.GetString() is { } s)
        {
            if (Num("minLength") is { } minLen && s.Length < minLen)
                Warn(diagnostics, path, $"Поле «{Title(field)}»: короче допустимого ({minLen} симв.).");
            if (Num("maxLength") is { } maxLen && s.Length > maxLen)
                Warn(diagnostics, path, $"Поле «{Title(field)}»: длиннее допустимого ({maxLen} симв.).");
            if (Str("pattern") is { } pattern && !Matches(s, pattern))
                Warn(diagnostics, path,
                    Str("patternMessage") is { } msg && msg.Length > 0
                        ? $"Поле «{Title(field)}»: {msg}"
                        : $"Поле «{Title(field)}» не соответствует формату типа «{primitive.Name}».");
        }

        if (primitive.BaseType == "date" && value.GetString() is { } d && !DateTimeOffset.TryParse(
                d, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
            Warn(diagnostics, path, $"Поле «{Title(field)}»: значение не разбирается как дата.");
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
        "bool" => "логическое значение",
        _ => "строка",
    };

    private static string Title(SchemaFieldInfo f) => f.Title ?? f.Key;

    private static void Warn(List<ResolutionDiagnostic> diagnostics, string path, string message)
        => diagnostics.Add(new ResolutionDiagnostic(DiagnosticSeverity.Warning, path, message, "value-type"));
}
