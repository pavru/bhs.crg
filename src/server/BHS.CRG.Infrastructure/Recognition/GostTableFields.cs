using System.Text.Json;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Domain.Schema;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>
/// Фиксированные наборы колонок таблиц ГОСТ-документов (спецификация/ведомость и кабельный журнал)
/// для распознавания одним vision-вызовом (по образцу таблицы товаров в счёте, см. InvoiceFields).
/// Колонки фиксированы под тип (решение пользователя) — надёжнее для распознавания и чище экспорт.
/// </summary>
public static class GostTableFields
{
    /// <summary>JSON-ключ, под которым распознаватель должен вернуть строки таблицы (JSON-массив).</summary>
    public const string RowsPath = "Строки";

    // Спецификация/ведомость оборудования и материалов (ГОСТ 21.110-2013, приближённо).
    public static readonly IReadOnlyList<RecognitionField> SpecificationColumns =
    [
        new("Позиция", "Позиция", "string"),
        new("Обозначение", "Обозначение (документа/типового проекта)", "string"),
        new("Наименование", "Наименование и техническая характеристика", "string"),
        new("ТипМарка", "Тип, марка, обозначение", "string"),
        new("Код", "Код оборудования/изделия/материала", "string"),
        new("Изготовитель", "Завод-изготовитель / поставщик", "string"),
        new("Единица", "Единица измерения", "string"),
        new("Количество", "Количество", "string"),
        new("Масса", "Масса единицы, кг", "string"),
        new("Примечание", "Примечание", "string"),
    ];

    // Кабельный журнал (ГОСТ 21.613 / общая практика, приближённо).
    public static readonly IReadOnlyList<RecognitionField> CableJournalColumns =
    [
        new("Номер", "Номер (обозначение) кабеля/провода", "string"),
        new("Откуда", "Начало линии (откуда)", "string"),
        new("Куда", "Конец линии (куда)", "string"),
        new("МаркаКабеля", "Марка кабеля/провода", "string"),
        new("Сечение", "Число и сечение жил", "string"),
        new("Длина", "Длина, м", "string"),
        new("СпособПрокладки", "Способ прокладки", "string"),
        new("Примечание", "Примечание", "string"),
    ];

    /// <summary>Колонки таблицы по функциональному тэгу документа, либо null (не таблица известного типа).</summary>
    public static IReadOnlyList<RecognitionField>? ColumnsForTag(string tag) => tag switch
    {
        FunctionalTag.GostDocSpecification => SpecificationColumns,
        FunctionalTag.GostDocCableJournal => CableJournalColumns,
        _ => null,
    };

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
