using System.Text.Json;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>
/// Чистая логика расщепления результата одного вызова распознавания счёта на оплату
/// (см. InvoiceFields) на шапку (плоские поля) и таблицу товаров (вложенный JSON-массив в
/// одном поле ответа) — вынесена отдельно от DataSetService ради юнит-тестируемости без БД/blob.
/// </summary>
public static class InvoiceRecognitionSplitter
{
    public static Dictionary<string, string?> SplitHeader(IReadOnlyDictionary<string, string?> values)
        => InvoiceFields.HeaderFields.ToDictionary(f => f.Path, f => values.GetValueOrDefault(f.Path));

    /// <summary>Сломанный/не-JSON ответ модели по товарам — не падаем, возвращаем пустой список
    /// (шапка при этом уже распознана независимо).</summary>
    public static List<Dictionary<string, string?>> SplitLineItems(IReadOnlyDictionary<string, string?> values)
    {
        var rows = new List<Dictionary<string, string?>>();
        if (!values.TryGetValue(InvoiceFields.LineItemsPath, out var json) || string.IsNullOrWhiteSpace(json))
            return rows;
        try
        {
            var parsed = JsonSerializer.Deserialize<List<Dictionary<string, string?>>>(json);
            if (parsed is not null) rows.AddRange(parsed);
        }
        catch (JsonException) { /* сломанный JSON от модели — пустой список товаров, не падаем */ }
        return rows;
    }
}
