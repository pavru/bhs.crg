using System.Text.Json;
using BHS.CRG.Application.QualityDocs;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>
/// Служебное таблиц документа: ключ массива строк и разбор ответа одним vision-вызовом (по образцу
/// таблицы товаров в счёте, см. InvoiceFields). Сами колонки — спецификации, кабельного журнала —
/// объявляет модуль-владелец профиля (issue #1075).
/// </summary>
public static class GostTableFields
{
    /// <summary>JSON-ключ, под которым распознаватель должен вернуть строки таблицы (JSON-массив).</summary>
    public const string RowsPath = "Строки";

    /// <summary>Поля для одного вызова распознавания: сами колонки (чтобы модель знала их смысл) +
    /// поле-массив <see cref="RowsPath"/> (чтобы ParseValues сохранил ответ таблицы).</summary>
    public static IReadOnlyList<RecognitionField> RecognitionFieldsFor(IReadOnlyList<RecognitionField> columns) =>
    [
        .. columns,
        new(RowsPath,
            "Строки таблицы — JSON-массив объектов с полями " + string.Join('/', columns.Select(c => c.Path)),
            "json-array"),
    ];

    /// <summary>Разбирает ответ распознавателя (JSON-массив под <see cref="RowsPath"/>) в строки,
    /// нормализованные к заданным колонкам. Сломанный/не-JSON ответ — пустой список (не падаем).</summary>
    public static List<Dictionary<string, string?>> SplitRows(
        IReadOnlyDictionary<string, string?> values, IReadOnlyList<RecognitionField> columns)
    {
        var result = new List<Dictionary<string, string?>>();
        if (!values.TryGetValue(RowsPath, out var json) || string.IsNullOrWhiteSpace(json))
            return result;
        List<Dictionary<string, string?>>? parsed;
        try { parsed = JsonSerializer.Deserialize<List<Dictionary<string, string?>>>(json); }
        catch (JsonException) { return result; }
        if (parsed is null) return result;

        foreach (var raw in parsed)
        {
            // Нормализуем к фиксированным колонкам (лишние ключи модели отбрасываем, недостающие — пусто).
            var row = columns.ToDictionary(c => c.Path, c => raw.GetValueOrDefault(c.Path));
            // Пропускаем полностью пустые строки (итоги/разделители).
            if (row.Values.Any(v => !string.IsNullOrWhiteSpace(v)))
                result.Add(row);
        }
        return result;
    }
}
