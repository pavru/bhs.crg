using System.Text.Json;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Значение маппинга колонки в привязке набора данных может быть двух видов:
///  • обычное — имя колонки файла (скалярное поле);
///  • ссылочное — составное поле, заполняемое ссылкой на запись каталога. Кодируется
///    строкой вида <c>@@ref:{"column":"ИНН","match":"ИНН","typeId":"&lt;guid&gt;"}</c>,
///    где column — колонка с искомым значением, match — поле записи каталога для
///    сопоставления (пусто = по отображаемому имени), typeId — составной тип каталога.
/// Формат разделяется с фронтендом (MappingEditor).
/// </summary>
public record DataSetRefMapping(string Column, string Match, Guid TypeId);

public static class DataSetMappingValue
{
    public const string RefPrefix = "@@ref:";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static bool IsRef(string? value) =>
        value is not null && value.StartsWith(RefPrefix, StringComparison.Ordinal);

    public static DataSetRefMapping? ParseRef(string? value)
    {
        if (!IsRef(value)) return null;
        try
        {
            var json = value![RefPrefix.Length..];
            var parsed = JsonSerializer.Deserialize<DataSetRefMapping>(json, JsonOpts);
            return parsed is null || parsed.TypeId == Guid.Empty || string.IsNullOrWhiteSpace(parsed.Column)
                ? null
                : parsed;
        }
        catch
        {
            return null;
        }
    }
}
