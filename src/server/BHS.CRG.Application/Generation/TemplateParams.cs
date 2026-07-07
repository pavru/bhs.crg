using System.Globalization;
using System.Text.Json;

namespace BHS.CRG.Application.Generation;

/// <summary>
/// Эффективные значения параметров шаблона для контекста генерации: дефолты объявления шаблона
/// (<see cref="Domain.Templates.Template.Parameters"/>), переопределённые значениями документа
/// (<see cref="Domain.Documents.DocumentInstance.TemplateParams"/>). Подмешиваются под ключ «params»
/// (в Typst: <c>data.params.имя</c>). Значения приводятся к объявленному типу (string/number/boolean),
/// чтобы в JSON попал корректный тип, а не строка.
/// </summary>
public static class TemplateParams
{
    public static Dictionary<string, object?> Effective(string? templateParametersJson, string? instanceOverridesJson)
    {
        var result = new Dictionary<string, object?>();
        if (string.IsNullOrWhiteSpace(templateParametersJson)) return result;

        var overrides = new Dictionary<string, JsonElement>();
        if (!string.IsNullOrWhiteSpace(instanceOverridesJson))
        {
            try
            {
                using var od = JsonDocument.Parse(instanceOverridesJson);
                if (od.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var p in od.RootElement.EnumerateObject())
                        overrides[p.Name] = p.Value.Clone();
            }
            catch { /* битые переопределения игнорируем — берём дефолты */ }
        }

        try
        {
            using var doc = JsonDocument.Parse(templateParametersJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var param in doc.RootElement.EnumerateArray())
            {
                if (param.ValueKind != JsonValueKind.Object) continue;
                if (!param.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) continue;
                var name = nameEl.GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                var type = param.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : "string";
                JsonElement? raw = overrides.TryGetValue(name, out var ov) ? ov
                    : param.TryGetProperty("default", out var def) ? def : null;
                result[name] = Coerce(raw, type);
            }
        }
        catch { /* битое объявление параметров — пустой набор */ }
        return result;
    }

    private static object? Coerce(JsonElement? el, string? type)
    {
        if (el is null || el.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return type == "number" ? 0d : type == "boolean" ? false : "";
        var e = el.Value;
        return type switch
        {
            "number" => e.ValueKind == JsonValueKind.Number ? e.GetDouble()
                : double.TryParse(e.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : 0d,
            "boolean" => e.ValueKind == JsonValueKind.True
                || (e.ValueKind == JsonValueKind.String && e.GetString()?.Trim().ToLowerInvariant() is "true" or "1"),
            _ => e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString(),
        };
    }
}
