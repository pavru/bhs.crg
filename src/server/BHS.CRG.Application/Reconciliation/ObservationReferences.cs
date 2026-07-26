using System.Text.Json;

namespace BHS.CRG.Application.Reconciliation;

/// <summary>
/// Ссылки замечания приходят от языковой модели, и она с одинаковой охотой пришлёт и объект, и строку
/// с тем же объектом внутри. На рабочих данных случилось именно второе: тринадцать замечаний легли в
/// журнал со ссылками-строкой, и потребитель, ждавший объект, не увидел ни одной — то есть находки
/// стали непроверяемы ровно из-за формата.
///
/// Поэтому строку с объектом внутри разворачиваем — и на приёме, и на чтении: разворот только на
/// приёме оставил бы уже записанные замечания сломанными.
/// </summary>
public static class ObservationReferences
{
    /// <summary>Строка с JSON-объектом внутри → сам объект. Остальное — как есть.</summary>
    public static JsonElement Unwrap(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) return value;

        var raw = value.GetString();
        if (string.IsNullOrWhiteSpace(raw)) return value;

        try
        {
            using var parsed = JsonDocument.Parse(raw);
            // Только объект и массив: строка «не смотрел» — это заметка, а не ссылки, и превращать её
            // в число или булево бессмысленно.
            return parsed.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? parsed.RootElement.Clone()
                : value;
        }
        catch (JsonException)
        {
            return value; // не JSON — значит и правда просто текст
        }
    }

    /// <summary>Есть ли в ссылках хоть что-то, по чему человек проверит утверждение.</summary>
    public static bool IsEmpty(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => true,
        JsonValueKind.Object => !value.EnumerateObject().Any(),
        JsonValueKind.Array => value.GetArrayLength() == 0,
        JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()),
        _ => false,
    };
}
