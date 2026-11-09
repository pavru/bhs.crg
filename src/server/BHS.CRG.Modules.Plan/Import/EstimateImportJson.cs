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

    public static (string Parsed, string Unparsed) Write(ParsedLocalEstimate model) => Write(model with { Unparsed = [] }, model.Unparsed);

    public static (string Parsed, string Unparsed) Write(ParsedConjuncture model) => Write(model with { Unparsed = [] }, model.Unparsed);

    public static (string Parsed, string Unparsed) Write(ParsedVolumeStatement model) => Write(model with { Unparsed = [] }, model.Unparsed);

    /// <summary>Модель ЛСР импорта; <c>null</c> — импорт не разобран как ЛСР.</summary>
    public static ParsedLocalEstimate? Model(EstimateImport import) =>
        Read<ParsedLocalEstimate>(import, GrandSmetaKind.LocalEstimate, (m, u) => m with { Unparsed = u });

    /// <summary>Пропущенные позиции импорта (D3 <c>plan-31</c>): из модели, пока она есть, — иначе записанные при
    /// создании редакции.</summary>
    public static IReadOnlyList<SkippedPosition> Skipped(EstimateImport import, ParsedLocalEstimate? model) =>
        model is not null ? EstimateImportRows.Skipped(model)
        : import.Skipped is null ? []
        : JsonSerializer.Deserialize<List<SkippedPosition>>(import.Skipped, Options)!;

    /// <summary>Модель конъюнктурного анализа импорта; <c>null</c> — импорт не разобран как КА.</summary>
    public static ParsedConjuncture? Conjuncture(EstimateImport import) =>
        Read<ParsedConjuncture>(import, GrandSmetaKind.Conjuncture, (m, u) => m with { Unparsed = u });

    /// <summary>Модель ведомости объёмов импорта; <c>null</c> — импорт не разобран как ВОР.</summary>
    public static ParsedVolumeStatement? VolumeStatement(EstimateImport import) =>
        Read<ParsedVolumeStatement>(import, GrandSmetaKind.VolumeStatement, (m, u) => m with { Unparsed = u });

    public static string Write(IReadOnlyList<SkippedPosition> skipped) => JsonSerializer.Serialize(skipped, Options);

    public static IReadOnlyList<Unparsed> Unparsed(EstimateImport import) =>
        import.Unparsed is null ? [] : JsonSerializer.Deserialize<List<Unparsed>>(import.Unparsed, Options)!;

    /// <summary>Модель без не разобранного и не разобранное — двумя колонками, одним правилом на все виды файла.</summary>
    private static (string Parsed, string Unparsed) Write<T>(T withoutUnparsed, IReadOnlyList<Unparsed> unparsed) =>
        (JsonSerializer.Serialize(withoutUnparsed, Options), JsonSerializer.Serialize(unparsed, Options));

    /// <summary>
    /// Модель вида <paramref name="kind" /> с не разобранным из своей колонки; импорт другого вида или без модели —
    /// <c>null</c>: колонка одна на все виды, и чужую модель читать нельзя.
    /// </summary>
    private static T? Read<T>(EstimateImport import, GrandSmetaKind kind, Func<T, IReadOnlyList<Unparsed>, T> withUnparsed)
        where T : class =>
        import.Parsed is null || import.Kind != kind
            ? null
            : withUnparsed(JsonSerializer.Deserialize<T>(import.Parsed, Options)!, Unparsed(import));
}
