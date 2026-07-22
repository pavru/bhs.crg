using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BHS.CRG.Application.Schema;

/// <summary>
/// Точечная правка JSON-дерева по пути аудита (issue #350): <c>ключ</c>, <c>a.b.c</c>, <c>Работы[0].Лишнее</c>.
/// Работает над мутабельным <see cref="JsonNode"/> (реквизиты инстанса). Используется командой применения
/// исправлений аудита — удалить осиротевший ключ / переименовать его в поле схемы.
/// </summary>
public static class JsonPathEditor
{
    private static readonly Regex Token = new(@"([^.\[\]]+)|\[(\d+)\]", RegexOptions.Compiled);

    /// <summary>Токены пути: строковый ключ или числовой индекс массива.</summary>
    private static List<object> Parse(string path)
    {
        var tokens = new List<object>();
        foreach (Match m in Token.Matches(path))
            tokens.Add(m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : m.Groups[1].Value);
        return tokens;
    }

    /// <summary>Родитель последнего токена + сам последний токен. null, если путь не разрешается.</summary>
    private static (JsonNode? parent, object last)? Navigate(JsonNode root, string path)
    {
        var tokens = Parse(path);
        if (tokens.Count == 0) return null;
        JsonNode? node = root;
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            if (node is null) return null;
            node = tokens[i] switch
            {
                string k when node is JsonObject o => o.TryGetPropertyValue(k, out var v) ? v : null,
                int idx when node is JsonArray a && idx >= 0 && idx < a.Count => a[idx],
                _ => null,
            };
        }
        return node is null ? null : (node, tokens[^1]);
    }

    /// <summary>Удаляет значение по пути. <paramref name="oldValue"/> — сериализованное старое значение
    /// (для журнала). false — путь не разрешился (значения уже нет).</summary>
    public static bool Remove(JsonNode root, string path, out string? oldValue)
    {
        oldValue = null;
        if (Navigate(root, path) is not var (parent, last)) return false;
        switch (last)
        {
            case string key when parent is JsonObject o && o.TryGetPropertyValue(key, out var v):
                oldValue = v?.ToJsonString();
                o.Remove(key);
                return true;
            case int idx when parent is JsonArray a && idx >= 0 && idx < a.Count:
                oldValue = a[idx]?.ToJsonString();
                a.RemoveAt(idx);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Переименовывает КЛЮЧ на его уровне в <paramref name="targetKey"/> — только если цель
    /// отсутствует/пуста (не затираем данные). false + <paramref name="skipReason"/>, если нельзя.</summary>
    public static bool Rename(JsonNode root, string path, string targetKey, out string? oldValue, out string? skipReason)
    {
        oldValue = null; skipReason = null;
        if (Navigate(root, path) is not var (parent, last) || last is not string key || parent is not JsonObject o)
        {
            skipReason = "Путь не разрешён или не является ключом объекта.";
            return false;
        }
        if (!o.TryGetPropertyValue(key, out var value))
        {
            skipReason = "Исходный ключ отсутствует.";
            return false;
        }
        if (o.TryGetPropertyValue(targetKey, out var existing) && existing is not null
            && !(existing is JsonObject eo && eo.Count == 0) && !(existing is JsonArray ea && ea.Count == 0))
        {
            skipReason = $"Целевое поле «{targetKey}» уже заполнено — перезапись не выполняется.";
            return false;
        }
        oldValue = value?.ToJsonString();
        o.Remove(key);
        o.Remove(targetKey);
        o[targetKey] = value?.DeepClone();
        return true;
    }
}
