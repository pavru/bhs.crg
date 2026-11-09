using System.Text.Json;
using System.Text.Json.Serialization;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.GrandSmeta;

namespace BHS.CRG.Modules.Plan.Import;

/// <summary>
/// Модель разбора у записи импорта — JSON, одним местом на запись и чтение (change <c>plan-07-estimate-import</c>).
/// Не разобранное лежит своей колонкой: отчёт перечисляет его, не поднимая модель целиком.
/// </summary>
internal static class EstimateImportJson
{
    /// <summary>Перечисления — именами: число в базе пережило бы перестановку значений, не сказав об этом.</summary>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static (string Parsed, string Unparsed) Write(ParsedLocalEstimate model) =>
        (JsonSerializer.Serialize(model with { Unparsed = [] }, Options), JsonSerializer.Serialize(model.Unparsed, Options));

    public static (string Parsed, string Unparsed) Write(ParsedConjuncture model) =>
        (JsonSerializer.Serialize(model with { Unparsed = [] }, Options), JsonSerializer.Serialize(model.Unparsed, Options));

    public static (string Parsed, string Unparsed) Write(ParsedVolumeStatement model) =>
        (JsonSerializer.Serialize(model with { Unparsed = [] }, Options), JsonSerializer.Serialize(model.Unparsed, Options));

    /// <summary>Модель ЛСР импорта; <c>null</c> — импорт не разобран как ЛСР.</summary>
    public static ParsedLocalEstimate? Model(EstimateImport import) =>
        import.Parsed is null || import.Kind != GrandSmetaKind.LocalEstimate
            ? null
            : JsonSerializer.Deserialize<ParsedLocalEstimate>(import.Parsed, Options)! with { Unparsed = Unparsed(import) };

    /// <summary>Пропущенные позиции импорта (D3 <c>plan-31</c>): из модели, пока она есть, — иначе записанные при
    /// создании редакции.</summary>
    public static IReadOnlyList<SkippedPosition> Skipped(EstimateImport import, ParsedLocalEstimate? model) =>
        model is not null ? EstimateImportRows.Skipped(model)
        : import.Skipped is null ? []
        : JsonSerializer.Deserialize<List<SkippedPosition>>(import.Skipped, Options)!;

    /// <summary>Модель конъюнктурного анализа импорта; <c>null</c> — импорт не разобран как КА.</summary>
    public static ParsedConjuncture? Conjuncture(EstimateImport import) =>
        import.Parsed is null || import.Kind != GrandSmetaKind.Conjuncture
            ? null
            : JsonSerializer.Deserialize<ParsedConjuncture>(import.Parsed, Options)! with { Unparsed = Unparsed(import) };

    /// <summary>Модель ведомости объёмов импорта; <c>null</c> — импорт не разобран как ВОР.</summary>
    public static ParsedVolumeStatement? VolumeStatement(EstimateImport import) =>
        import.Parsed is null || import.Kind != GrandSmetaKind.VolumeStatement
            ? null
            : JsonSerializer.Deserialize<ParsedVolumeStatement>(import.Parsed, Options)! with { Unparsed = Unparsed(import) };

    public static string Write(IReadOnlyList<SkippedPosition> skipped) => JsonSerializer.Serialize(skipped, Options);

    public static IReadOnlyList<Unparsed> Unparsed(EstimateImport import) =>
        import.Unparsed is null ? [] : JsonSerializer.Deserialize<List<Unparsed>>(import.Unparsed, Options)!;
}
