namespace BHS.CRG.Application.QualityDocs;

/// <summary>
/// Нормализация ключа идентичности материала для связи с документом качества.
/// Одинаково применяется при создании связи и при подмешивании на генерации.
/// </summary>
public static class MaterialKeyNormalizer
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        // регистр, окружающие/повторяющиеся пробелы игнорируем
        var collapsed = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.ToLowerInvariant();
    }
}
