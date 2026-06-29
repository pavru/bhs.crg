using System.Text;
using System.Text.Json;
using BHS.CRG.Application.QualityDocs;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>Общие для всех движков распознавания части: промпт и разбор ответа.</summary>
public static class RecognitionShared
{
    public static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    { "image/png", "image/jpeg", "image/jpg", "image/webp", "image/gif" };

    public static string NormalizeImageMime(string mime)
        => string.Equals(mime, "image/jpg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : mime.ToLowerInvariant();

    public static string BuildPrompt(IReadOnlyList<RecognitionField> fields)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ты извлекаешь реквизиты из скан-копии документа (сертификат/декларация соответствия и т.п.).");
        sb.AppendLine("Извлеки значения СТРОГО для перечисленных полей. Ответ — один JSON-объект {\"путь\": \"значение\"} без markdown и пояснений.");
        sb.AppendLine("Даты возвращай в ISO (ГГГГ-ММ-ДД). Если значения нет — пустая строка. Не выдумывай.");
        sb.AppendLine();
        sb.AppendLine("Поля (путь — название — тип):");
        foreach (var f in fields)
        {
            sb.Append("- ").Append(f.Path).Append(" — ").Append(f.Title).Append(" — ").Append(f.Type);
            if (f.Options is { Count: > 0 }) sb.Append(" (варианты: ").Append(string.Join(", ", f.Options)).Append(')');
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static IReadOnlyDictionary<string, string?> ParseValues(string text, IReadOnlyList<RecognitionField> fields)
    {
        var result = new Dictionary<string, string?>();
        var jsonText = StripFences(text).Trim();
        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var v = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Null => null,
                        JsonValueKind.Number => prop.Value.GetRawText(),
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        _ => prop.Value.GetRawText(),
                    };
                    if (!string.IsNullOrWhiteSpace(v)) result[prop.Name] = v;
                }
        }
        catch (JsonException) { /* не-JSON ответ — вернём пусто */ }

        var allowed = fields.Select(f => f.Path).ToHashSet();
        return result.Where(kv => allowed.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public static string StripFences(string s)
    {
        s = s.Trim();
        if (!s.StartsWith("```")) return s;
        var firstNl = s.IndexOf('\n');
        if (firstNl < 0) return s;
        var inner = s[(firstNl + 1)..];
        var lastFence = inner.LastIndexOf("```", StringComparison.Ordinal);
        return lastFence >= 0 ? inner[..lastFence] : inner;
    }

    public static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}

/// <summary>Один движок распознавания (Anthropic/Gemini/Ollama). Возвращает СЫРОЙ текст модели.</summary>
public interface IRecognizerEngine
{
    string Name { get; }
    Task<string> RecognizeRawAsync(byte[] file, string mimeType, IReadOnlyList<RecognitionField> fields, CancellationToken ct = default);
}
