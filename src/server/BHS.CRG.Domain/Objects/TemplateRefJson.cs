using System.Text.Json;
using System.Text.Json.Nodes;

namespace BHS.CRG.Domain.Objects;

/// <summary>
/// Разбор ссылок документа на шаблоны — выбора (массив id) и переопределений (объект с id-ключами),
/// issue #1960. Одно место на запись, уборку при восстановлении и удаление шаблона: правило «что
/// считается ссылкой» не должно расходиться между ними — генерация, SQL миграции и домен ищут ссылку
/// в одном виде.
/// </summary>
public static class TemplateRefJson
{
    /// <summary>Ссылка на шаблон — id в виде «D» (<c>xxxxxxxx-xxxx-…</c>, регистр не важен), как его
    /// пишет и ищет система; иной вид (N, B, P) — не ссылка: генерация такого ключа не найдёт.</summary>
    public static Guid? TemplateKey(string? text) =>
        text is not null && Guid.TryParseExact(text, "D", out var id) ? id : null;

    /// <summary>Объект JSON — или null: не объект, нечитаемый, повтор ключа. <see cref="JsonObject"/>
    /// разбирает лениво: повтор ключа бросает не <see cref="JsonException"/> при разборе, а
    /// <see cref="ArgumentException"/> при первом обращении — его ловим здесь же.</summary>
    public static JsonObject? TryParseObject(string json)
    {
        try
        {
            var obj = JsonNode.Parse(json) as JsonObject;
            _ = obj?.Count;
            return obj;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { return null; }
    }

    /// <summary>Массив JSON — или null, если это не массив или он нечитаем.</summary>
    public static JsonArray? TryParseArray(string json)
    {
        try { return JsonNode.Parse(json) as JsonArray; }
        catch (JsonException) { return null; }
    }

    /// <summary>Элемент выбора как ссылка: строка в виде «D» — или null (число, null, иной вид).</summary>
    public static Guid? TemplateKey(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? TemplateKey(v.GetValue<string>()) : null;

    /// <summary>
    /// Выбор шаблонов (<c>TemplateIds</c>) по порядку, без повторов. Элемент, который ссылкой не
    /// является, пропускается, а не роняет весь выбор. Одно правило у домена (пин, пересадка, снятие),
    /// выбора шаблона выпуска и сборки комплекта: иначе генерация видела бы выбор, которого домен не
    /// видит, — и новая версия не пересаживала бы его, а удаление не снимало бы.
    /// </summary>
    public static List<Guid> SelectionIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        return TryParseArray(json)?
            .Select(TemplateKey)
            .OfType<Guid>()
            .Distinct()
            .ToList() ?? [];
    }
}
