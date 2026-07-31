using System.Text.Json;
using System.Text.Json.Nodes;

namespace BHS.CRG.Application.Documents;

/// <summary>
/// Вырезание тяжёлой полезной нагрузки из СПИСОЧНЫХ ответов (issue #520).
///
/// Зачем: <c>GET /api/common-data</c> отдавал 5,43 МБ, из них 99,6 % — base64 картинок, при том что
/// список при отрисовке сам отбрасывает объекты (<c>typeof v !== 'object'</c>) и показывает только
/// скаляры. То есть провод грузили тем, что клиент осознанно не показывает.
///
/// Почему не «просто не отдавать Data»: по ключам записи считается покрытие базовым экземпляром
/// (<c>Object.keys(entry.data)</c>) — без них поля, приходящие наследованием, начали бы требовать
/// заполнения вручную. Это было бы тихим изменением наследования, а не экономией трафика. Поэтому
/// форма и КЛЮЧИ сохраняются, вырезается только сама data-URI; опции картинки (width/height/align/
/// fit) остаются — они лёгкие и по ним строится подпись.
///
/// Применять ТОЛЬКО в списочных местах и никогда в <c>CommonDataEntryDto.From</c>: тот обслуживает и
/// чтение одной записи, и ответы POST/PUT — там отсечение молча обрезало бы редактор и round-trip.
/// </summary>
public static class HeavyLeafElision
{
    /// <summary>Маркер вырезанного значения. Не «пустая строка» и не отсутствие ключа — по нему видно, что данные есть, но не отданы.</summary>
    public const string ElidedMarker = "$elided";

    /// <summary>Значение маркера для картинки.</summary>
    public const string ImageKind = "image";

    private const string DataUriPrefix = "data:image";

    /// <summary>
    /// Копия документа без data-URI картинок. Возвращает ТОТ ЖЕ экземпляр, если резать нечего —
    /// подавляющее большинство записей картинок не имеет, и платить за них копированием незачем.
    /// </summary>
    public static JsonDocument WithoutHeavyLeaves(JsonDocument data)
    {
        var node = JsonNode.Parse(data.RootElement.GetRawText());
        if (node is null || !Elide(node)) return data;
        return JsonDocument.Parse(node.ToJsonString());
    }

    /// <summary>Обходит дерево, заменяя тяжёлые листья. true — что-то вырезано.</summary>
    private static bool Elide(JsonNode node)
    {
        var changed = false;
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var child = obj[key];
                    if (IsDataUri(child))
                    {
                        // Голая строка (легаси-форма значения) — заменяем маркером-объектом: опций у
                        // неё нет, а форма объекта совпадает с той, что получится из {src, ...}.
                        obj[key] = new JsonObject { [ElidedMarker] = ImageKind };
                        changed = true;
                    }
                    else if (child is JsonObject inner && IsDataUri(inner["src"]))
                    {
                        inner.Remove("src");
                        inner[ElidedMarker] = ImageKind;   // опции остаются на месте
                        changed = true;
                    }
                    else if (child is not null && Elide(child))
                    {
                        changed = true;
                    }
                }
                break;

            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var item = arr[i];
                    if (IsDataUri(item))
                    {
                        arr[i] = new JsonObject { [ElidedMarker] = ImageKind };
                        changed = true;
                    }
                    else if (item is not null && Elide(item))
                    {
                        changed = true;
                    }
                }
                break;
        }
        return changed;
    }

    private static bool IsDataUri(JsonNode? node) =>
        node is JsonValue v
        && v.TryGetValue<string>(out var s)
        && s.StartsWith(DataUriPrefix, StringComparison.Ordinal);
}
