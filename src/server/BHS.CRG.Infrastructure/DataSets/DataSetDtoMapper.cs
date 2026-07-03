using System.Text.Json;
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

    // Значение ячейки для предпросмотра. Для ссылочного маппинга (@@ref) показываем
    // искомое значение колонки с маркером — фактический резолвинг в каталог выполняется
    // при генерации. Для файлового маппинга (@@file) — уже полноценный объект-вложение
    // (используется напрямую и при синхронизации CommonDataEntry.Data, не только для показа).
    public static object? PreviewCell(string mapVal, IReadOnlyDictionary<string, string?>? row)
    {
        var fileMap = DataSetMappingValue.ParseFile(mapVal);
        if (fileMap is not null)
            return row is null ? null : DataSetMappingValue.ResolveFileValue(fileMap, row);

        var refMap = DataSetMappingValue.ParseRef(mapVal);
        if (refMap is not null)
        {
            var v = row != null && row.TryGetValue(refMap.Column, out var lv) ? lv : null;
            return string.IsNullOrWhiteSpace(v) ? null : $"🔗 {v}";
        }
        return row != null && row.TryGetValue(mapVal, out var val) ? val : null;
    }

    public static DataSetFileDto MapFile(DataSetFile f) => new(
        f.Id, f.Name, f.Format.ToString(), f.Scope.ToString(), f.ScopeId,
        f.Sources.Select(MapSource).ToList(), f.CreatedAt);

    public static DataSetSourceDto MapSource(DataSetSource s) => new(
        s.Id, s.FileId, s.Name, s.SheetOrPath, s.ColumnExpressions, s.CachedSchema, s.CachedRowCount,
        DeserializeJson(s.RowFilter), DeserializeJson(s.ComputedColumns), DeserializeJson(s.SortSpec),
        s.Tags is null ? null : JsonSerializer.Deserialize<List<string>>(s.Tags));

    public static DataSetBindingDto MapBinding(DataSetBinding b) => new(
        b.Id, b.InstanceId, b.CommonDataEntryId, b.SourceId, b.TargetFieldKey,
        JsonSerializer.Deserialize<Dictionary<string, string>>(b.Mapping) ?? [],
        b.Source is null ? null : new BindingSourceDto(
            b.Source.Id, b.Source.Name, b.Source.SheetOrPath, b.Source.CachedSchema, b.Source.CachedRowCount,
            b.Source.File is null ? null : new BindingFileDto(
                b.Source.File.Id, b.Source.File.Name, b.Source.File.Format.ToString(),
                b.Source.File.Scope.ToString(), b.Source.File.ScopeId)));

    public static DataSetBindingTemplateDto MapTemplate(DataSetBindingTemplate t) => new(
        t.Id, t.DocumentTypeId, t.Name, t.TargetFieldKey,
        JsonSerializer.Deserialize<Dictionary<string, string>>(t.ColumnMappings) ?? [],
        t.SortOrder, t.CreatedAt, t.UpdatedAt);

    public static DataSetProcessingTemplateDto MapProcessingTemplate(DataSetProcessingTemplate t) => new(
        t.Id, t.Name, t.SheetOrPath, t.ColumnExpressions,
        DeserializeJson(t.RowFilter), DeserializeJson(t.ComputedColumns), DeserializeJson(t.SortSpec),
        t.CreatedAt, t.UpdatedAt);
}
