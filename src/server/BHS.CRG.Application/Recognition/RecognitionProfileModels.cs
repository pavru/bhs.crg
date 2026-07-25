using System.Text.Json;
using System.Text.Json.Serialization;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Domain.Recognition;

namespace BHS.CRG.Application.Recognition;

/// <summary>
/// Одно поле/колонка профиля распознавания (issue #406). <see cref="Description"/> — не украшение:
/// это ЕДИНСТВЕННЫЙ канал смысла в промпт, потому что <see cref="RecognitionField"/> отдельного
/// описания не имеет, и <c>AppendCommonInstructions</c> печатает строку «путь — название — тип»
/// именно из <c>Title</c>. Поэтому <see cref="Name"/> → <c>Path</c>, <see cref="Description"/> → <c>Title</c>.
///
/// Признака «системное» здесь НЕТ намеренно: он производный от кодового дескриптора вида
/// (<c>SystemFieldNames</c>), а не хранимое пользовательское данное — иначе флаг снимался бы через
/// импорт/восстановление бэкапа и защита несущих полей обходилась бы.
/// </summary>
/// <param name="Name">JSON-ключ, который должен вернуть распознаватель (он же имя колонки источника).</param>
/// <param name="Description">Смысловая подсказка модели; при отсутствии в промпт уходит <paramref name="Name"/>.</param>
/// <param name="Type">string | number | date. По умолчанию string.</param>
/// <param name="Options">Закрытый список допустимых значений (печатается в промпт как «варианты: …»).</param>
public record RecognitionProfileField(
    string Name,
    string? Description = null,
    string? Type = null,
    IReadOnlyList<string>? Options = null);

/// <summary>
/// Структурные подсказки о ФОРМЕ таблицы — то, что набором колонок не выразить. Намеренно закрытый
/// набор флагов, а не свободный текст: пользователь задаёт ПАРАМЕТРЫ, промпты пишем мы.
/// </summary>
/// <param name="TwoTierHeader">Двухэтажная шапка: колонки сгруппированы под общими заголовками.</param>
/// <param name="PairedSections">Парные секции (напр. «по проекту» / «фактически») с повторяющимися подколонками.</param>
/// <param name="SkipTotals">Не включать итоговые строки (поведение общего табличного промпта по умолчанию).</param>
public record RecognitionTableShape(
    bool TwoTierHeader = false,
    bool PairedSections = false,
    bool SkipTotals = true);

/// <summary>Профиль, разобранный в готовые к вызову распознавателя параметры.</summary>
/// <param name="Fields">Скалярные поля (графы штампа, шапка счёта). Для чисто табличных видов пусто.</param>
/// <param name="RowColumns">Колонки табличной части того же вызова; пусто — таблицы нет.</param>
public record ResolvedRecognitionProfile(
    Guid Id,
    string Name,
    RecognitionProfileKind Kind,
    IReadOnlyList<RecognitionProfileField> Fields,
    IReadOnlyList<RecognitionProfileField> RowColumns,
    RecognitionTableShape? Shape)
{
    /// <summary>Скалярные поля профиля в форме, которую принимает распознаватель.</summary>
    public IReadOnlyList<RecognitionField> ToRecognitionFields() => Map(Fields);

    /// <summary>Колонки табличной части в форме, которую принимает распознаватель.</summary>
    public IReadOnlyList<RecognitionField> ToRowColumns() => Map(RowColumns);

    private static IReadOnlyList<RecognitionField> Map(IReadOnlyList<RecognitionProfileField> src) =>
        [.. src.Select(f => new RecognitionField(
            f.Name,
            string.IsNullOrWhiteSpace(f.Description) ? f.Name : f.Description,
            string.IsNullOrWhiteSpace(f.Type) ? "string" : f.Type,
            f.Options is { Count: > 0 } ? f.Options : null))];
}

/// <summary>(Де)сериализация параметров профиля в/из jsonb.</summary>
public static class RecognitionProfileJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IReadOnlyList<RecognitionProfileField> ReadFields(JsonDocument? fields)
    {
        if (fields is null) return [];
        try
        {
            return JsonSerializer.Deserialize<List<RecognitionProfileField>>(fields.RootElement.GetRawText(), Options)
                   ?? [];
        }
        catch (JsonException) { return []; }
    }

    public static RecognitionTableShape? ReadShape(JsonDocument? shape)
    {
        if (shape is null) return null;
        try { return JsonSerializer.Deserialize<RecognitionTableShape>(shape.RootElement.GetRawText(), Options); }
        catch (JsonException) { return null; }
    }

    public static JsonDocument WriteFields(IEnumerable<RecognitionProfileField> fields)
        => JsonDocument.Parse(JsonSerializer.Serialize(fields, Options));

    public static JsonDocument? WriteFieldsOrNull(IReadOnlyList<RecognitionProfileField>? fields)
        => fields is null || fields.Count == 0 ? null : WriteFields(fields);

    public static JsonDocument? WriteShape(RecognitionTableShape? shape)
        => shape is null ? null : JsonDocument.Parse(JsonSerializer.Serialize(shape, Options));

    public static ResolvedRecognitionProfile Resolve(RecognitionProfile profile) => new(
        profile.Id, profile.Name, profile.Kind,
        ReadFields(profile.Fields), ReadFields(profile.RowColumns), ReadShape(profile.Shape));

    /// <summary>Каноничный текст параметров — для сравнения «отличается ли от заводского». Обе стороны
    /// прогоняются через одну модель и сериализатор, поэтому нормализация jsonb в PostgreSQL
    /// (переупорядочивание ключей) не создаёт ложных различий.</summary>
    public static string Canonical(
        IReadOnlyList<RecognitionProfileField> fields,
        IReadOnlyList<RecognitionProfileField> rowColumns,
        RecognitionTableShape? shape)
        => JsonSerializer.Serialize(new { fields, rowColumns, shape }, Options);
}
