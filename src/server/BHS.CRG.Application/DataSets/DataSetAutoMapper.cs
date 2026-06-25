namespace BHS.CRG.Application.DataSets;

public record FieldInfo(string Key, string Title);

public static class DataSetAutoMapper
{
    /// <summary>
    /// Предлагает маппинг колонок файла на поля документа.
    /// Приоритет: точное совпадение → без учёта регистра → колонка содержит ключ или наоборот.
    /// Возвращает { "ключПоля": "НазваниеКолонки" }.
    /// </summary>
    public static Dictionary<string, string> AutoMap(
        IReadOnlyList<string> columns,
        IReadOnlyList<FieldInfo> fields)
    {
        var result = new Dictionary<string, string>();
        foreach (var field in fields)
        {
            var match = FindBestColumn(columns, field.Key, field.Title);
            if (match != null)
                result[field.Key] = match;
        }
        return result;
    }

    private static string? FindBestColumn(IReadOnlyList<string> columns, string key, string title)
    {
        // 1. Exact match on key
        var exact = columns.FirstOrDefault(c => c == key);
        if (exact != null) return exact;

        // 2. Case-insensitive match on key
        var ciKey = columns.FirstOrDefault(c =>
            string.Equals(c, key, StringComparison.OrdinalIgnoreCase));
        if (ciKey != null) return ciKey;

        // 3. Case-insensitive match on title
        if (!string.IsNullOrWhiteSpace(title))
        {
            var ciTitle = columns.FirstOrDefault(c =>
                string.Equals(c, title, StringComparison.OrdinalIgnoreCase));
            if (ciTitle != null) return ciTitle;
        }

        // 4. Column contains key or key contains column (substring, case-insensitive)
        var sub = columns.FirstOrDefault(c =>
            c.Contains(key, StringComparison.OrdinalIgnoreCase) ||
            key.Contains(c, StringComparison.OrdinalIgnoreCase));
        if (sub != null) return sub;

        return null;
    }
}
