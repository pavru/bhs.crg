using System.Text.Json;
using System.Text.RegularExpressions;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>
/// Код ошибки из ответа поставщика — для текста отказа (issue #1343).
///
/// <para>Тело ответа в текст отказа не идёт (issue #1050): его пишет сторонний сервис, и что в нём
/// окажется, мы не решаем. Но без кода «Gemini ответил 400» не говорит ничего — неверный ключ, регион
/// и негодный документ отвечают одним числом. Код же — слово из закрытого словаря поставщика
/// (<c>INVALID_ARGUMENT</c>, <c>invalid_request_error</c>). Поэтому берётся только то, что выглядит
/// кодом: латиница и подчёркивание, не длиннее сорока знаков; иное — <c>null</c>. Сообщение
/// поставщика (<c>error.message</c>) не берётся вовсе: это свободный текст.</para>
/// </summary>
public static partial class ProviderErrorCode
{
    [GeneratedRegex("^[A-Za-z_]{2,40}$")]
    private static partial Regex Code();

    /// <summary>Код по пути в JSON ответа (<c>error.status</c>, <c>error.type</c>) или <c>null</c>.</summary>
    public static string? Of(string json, params string[] path)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var node = doc.RootElement;
            foreach (var step in path)
                if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(step, out node))
                    return null;
            return node.ValueKind == JsonValueKind.String && node.GetString() is { } code && Code().IsMatch(code)
                ? code
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Хвост текста отказа: « (КОД)» или ничего.</summary>
    public static string Suffix(string? code) => code is null ? "" : $" ({code})";
}
