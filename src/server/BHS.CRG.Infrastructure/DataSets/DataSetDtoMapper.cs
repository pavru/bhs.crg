using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.DataSets;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Чистые функции DataSets-модуля, вынесенные из <see cref="DataSetService"/>: определение
/// формата файла по расширению, (де)сериализация JSON-полей сущностей и маппинг domain → DTO.
/// Без зависимостей (db/blob) — первый шаг декомпозиции God Object'а DataSetService
/// (см. архитектурный отчёт, «Предложение 3: декомпозиция DataSetService»).
/// </summary>
public static class DataSetDtoMapper
{
    /// <summary>Имя файла без запрещённых символов (для скачиваемых выгрузок/разрезанных PDF).</summary>
    public static string SanitizeFileName(string name) => FileNames.Sanitize(name, "данные");

    public static DataSetFormat? DetectFormat(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".csv" or ".txt"  => DataSetFormat.Csv,
            ".xlsx"           => DataSetFormat.Xlsx,
            ".xls"            => DataSetFormat.Xls,
            ".xml"            => DataSetFormat.Xml,
            ".json"           => DataSetFormat.Json,
            ".zip" or ".gsfx" => DataSetFormat.Zip,
            ".pdf"            => DataSetFormat.Pdf,
            _                 => null,
        };

    public static string SerializeSchema(IReadOnlyList<DataSetColumnInfo> columns) =>
        JsonSerializer.Serialize(columns.Select(c => new { name = c.Name, sampleValues = c.SampleValues }));

    public static string? SerializeColumnExpressions(IReadOnlyList<ColumnExprDto>? columnExpressions) =>
        columnExpressions is { Count: > 0 }
            ? JsonSerializer.Serialize(columnExpressions.Select(c => new { name = c.Name, expr = c.Expr }))
            : null;

    public static string SerializeMapping(Dictionary<string, string>? mapping) =>
        JsonSerializer.Serialize(mapping ?? new Dictionary<string, string>());

    public static string? SerializeJson(object? value) =>
        value is null ? null : JsonSerializer.Serialize(value);

    public static object? DeserializeJson(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<object>(json);

    // Значение ячейки для предпросмотра — через общий DataSetMappingApplier (issue #374). Для ссылочного
    // маппинга (@@ref) показываем искомое значение колонки с маркером «🔗 …» — фактический резолвинг в
    // каталог выполняется при генерации. Для @@file — полноценный объект-вложение; для @@inline —
    // встроенный объект (под-поля рекурсивно, @@ref-под-поля тоже как маркеры).
    public static Task<object?> PreviewCellAsync(string mapVal, IReadOnlyDictionary<string, string?>? row, CancellationToken ct = default)
        => DataSetMappingApplier.ApplyAsync(mapVal, row, PreviewRefMarker, path: "", ct);

    private static Task<object?> PreviewRefMarker(DataSetRefMapping refMap, IReadOnlyDictionary<string, string?> row, string path, CancellationToken ct)
    {
        var v = refMap.IsIdentity
            ? string.Join(" · ", refMap.IdentityColumns!.Values
                .Select(c => row.TryGetValue(c, out var cv) ? cv : null)
                .Where(s => !string.IsNullOrWhiteSpace(s)))
            : refMap.Column != null && row.TryGetValue(refMap.Column, out var lv) ? lv : null;
        return Task.FromResult<object?>(string.IsNullOrWhiteSpace(v) ? null : $"🔗 {v}");
    }

    /// <param name="bindingCounts">Число привязок по id источника (issue #417). null — не считали.</param>
    /// <param name="liveStates">Живое состояние системных источников по id (issue #613, #626) — для
    /// наборов, где кэш поддерживать нечем. Источники не из словаря отдают свой кэш.</param>
    public static DataSetFileDto MapFile(DataSetFile f, IReadOnlyDictionary<Guid, int>? bindingCounts = null,
        IReadOnlyDictionary<Guid, SystemSourceCounter.SystemSourceState>? liveStates = null) => new(
        f.Id, f.Name, f.Format.ToString(), f.Scope.ToString(), f.ScopeId,
        f.Sources.Select(s => MapSource(s, BindingCountOf(bindingCounts, s.Id), LiveStateOf(liveStates, s.Id))).ToList(),
        f.CreatedAt, f.PreprocessingProfile,
        f.RecognitionProfiles is null ? null
            : JsonSerializer.Deserialize<Dictionary<string, Guid>>(f.RecognitionProfiles));

    public static DataSetSourceDto MapSource(
        DataSetSource s, int? bindingCount = null, SystemSourceCounter.SystemSourceState? live = null) => new(
        s.Id, s.FileId, s.Name, s.SheetOrPath, s.ColumnExpressions, LiveSchemaOf(live) ?? s.CachedSchema,
        live?.RowCount ?? s.CachedRowCount,
        DeserializeJson(s.RowFilter), DeserializeJson(s.ComputedColumns), DeserializeJson(s.SortSpec),
        s.Tags is null ? null : JsonSerializer.Deserialize<List<string>>(s.Tags), s.RecognitionStale,
        s.MaterializeTypeId,
        s.MaterializeMapping is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(s.MaterializeMapping),
        bindingCount, live?.Warning,
        MaterializeVariantSelector.ParseConfig(s.MaterializeDiscriminator),
        s.MaterializeByIdColumn, s.Origin, s.StaleReason);

    /// <summary>null, если счётчики не запрашивали (одиночная мутация), иначе 0 для источника без привязок.</summary>
    private static int? BindingCountOf(IReadOnlyDictionary<Guid, int>? counts, Guid sourceId)
        => counts is null ? null : counts.GetValueOrDefault(sourceId);

    /// <summary>null (отдать кэш), если состояние не пересчитывали или источник не системный —
    /// в отличие от привязок «нет в словаре» здесь НЕ значит «ноль строк».</summary>
    private static SystemSourceCounter.SystemSourceState? LiveStateOf(
        IReadOnlyDictionary<Guid, SystemSourceCounter.SystemSourceState>? states, Guid sourceId)
        => states is not null && states.TryGetValue(sourceId, out var st) ? st : null;

    /// <summary>
    /// Схема системного источника на момент чтения (issue #664) — в том же виде, в каком её ждёт
    /// клиент от <c>CachedSchema</c>, чтобы диалог привязки предлагал колонки, которые в строках
    /// действительно есть. Кэш обновить нечем: файла у набора нет, определение не редактируется, —
    /// а колонки провайдера зависят от схемы типа и меняются вместе с ней.
    ///
    /// null (отдать кэш) и на пустом списке — см. те же соображения в <c>DataSnapshotService</c>.
    /// </summary>
    private static string? LiveSchemaOf(SystemSourceCounter.SystemSourceState? live)
        => live is { Columns.Count: > 0 } l ? SerializeSchema(l.Columns) : null;

    public static DataSetBindingDto MapBinding(DataSetBinding b) => new(
        b.Id, b.OwnerId, b.SourceId, b.TargetFieldKey,
        JsonSerializer.Deserialize<Dictionary<string, string>>(b.Mapping) ?? [],
        b.Source is null ? null : new BindingSourceDto(
            b.Source.Id, b.Source.Name, b.Source.SheetOrPath, b.Source.CachedSchema, b.Source.CachedRowCount,
            b.Source.File is null ? null : new BindingFileDto(
                b.Source.File.Id, b.Source.File.Name, b.Source.File.Format.ToString(),
                b.Source.File.Scope.ToString(), b.Source.File.ScopeId),
            b.Source.MaterializeTypeId,
            b.Source.MaterializeMapping is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(b.Source.MaterializeMapping),
            b.Source.Origin));

    public static DataSetBindingTemplateDto MapTemplate(DataSetBindingTemplate t) => new(
        t.Id, t.DocumentTypeId, t.Name, t.TargetFieldKey,
        JsonSerializer.Deserialize<Dictionary<string, string>>(t.ColumnMappings) ?? [],
        t.SortOrder, t.CreatedAt, t.UpdatedAt);

    public static DataSetProcessingTemplateDto MapProcessingTemplate(DataSetProcessingTemplate t) => new(
        t.Id, t.Name, t.SheetOrPath, t.ColumnExpressions,
        DeserializeJson(t.RowFilter), DeserializeJson(t.ComputedColumns), DeserializeJson(t.SortSpec),
        t.CreatedAt, t.UpdatedAt);
}
