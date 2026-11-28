using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.DataSets;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>Метаданные табличного источника требуют живых ворот, даже когда поставщик отсутствует.</summary>
internal static class TableSourceMetadata
{
    public static bool IsTable(DataSetSource source) =>
        source.File is { IsSystem: true } && IsTableMarker(source.SheetOrPath);

    public static bool IsTableMarker(string marker) =>
        marker.StartsWith(SystemDataSets.TableMarkerPrefix, StringComparison.OrdinalIgnoreCase);

    public static string Schema(DataSetSource source, string? live) =>
        live ?? (IsTable(source) ? "[]" : source.CachedSchema);

    public static int? RowCount(DataSetSource source, int? live) =>
        live ?? (IsTable(source) ? null : source.KnownCachedRowCount());

    /// <summary>Для табличного источника отсутствие живой схемы и пустая схема запрещают подстановку кэша.</summary>
    public static IReadOnlyList<DataSetColumnInfo>? Columns(
        DataSetSource source, IReadOnlyList<DataSetColumnInfo>? live) =>
        IsTable(source) ? live ?? [] : live is { Count: > 0 } ? live : null;

    /// <summary>Из старой схемы в новую запись или копию переносятся только имена колонок.</summary>
    public static string StructuralSchema(string cachedSchema)
    {
        try
        {
            using var json = JsonDocument.Parse(cachedSchema);
            if (json.RootElement.ValueKind != JsonValueKind.Array) return "[]";
            return DataSetDtoMapper.SerializeSchema(
                [.. json.RootElement.EnumerateArray()
                    .Where(c => c.ValueKind == JsonValueKind.Object && c.TryGetProperty("name", out var name)
                        && name.ValueKind == JsonValueKind.String)
                    .Select(c => new DataSetColumnInfo(c.GetProperty("name").GetString()!, []))]);
        }
        catch (JsonException)
        {
            return "[]";
        }
    }
}
