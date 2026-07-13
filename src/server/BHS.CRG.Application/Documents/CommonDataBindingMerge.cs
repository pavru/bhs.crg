using System.Text.Json;

namespace BHS.CRG.Application.Documents;

/// <summary>
/// Мёрджит РЕЗОЛВНУТЫЕ значения привязок (см. IDataSetResolver.ResolveOwnerBindingsAsync) в данные
/// объекта общих данных перед сохранением (sync-on-save). Ключевое (issue #99): значения приходят
/// уже как ЗНАЧЕНИЯ резолва — скаляр = строка, @@ref = {$ref:catalog, entryId} (настоящая ссылка),
/// — а не как display-превью «🔗 …». Data читается EntityResolver живьём при каждом $ref, поэтому
/// синхронизация — на момент сохранения.
/// </summary>
public static class CommonDataBindingMerge
{
    public static JsonDocument Merge(JsonDocument current, IReadOnlyDictionary<string, object?> resolved)
    {
        var dict = new Dictionary<string, JsonElement>();
        foreach (var prop in current.RootElement.EnumerateObject())
            dict[prop.Name] = prop.Value.Clone();

        foreach (var (key, value) in resolved)
        {
            // Пустое строковое значение колонки не затирает ранее введённое вручную (источник мог
            // временно не дать данных). Нет-матч @@ref сюда не попадает (резолвер его пропускает).
            if (value is null) continue;
            if (value is string s && string.IsNullOrEmpty(s)) continue;
            dict[key] = value is JsonElement je ? je.Clone() : JsonSerializer.SerializeToElement(value);
        }

        return JsonDocument.Parse(JsonSerializer.Serialize(dict));
    }
}
