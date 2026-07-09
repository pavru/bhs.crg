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

/// <summary>
/// Файловый маппинг — поле типа "file" заполняется вложением, синтезированным из колонок ТОЙ ЖЕ
/// строки источника (в отличие от ref-маппинга — здесь нет cross-table lookup). Column — колонка
/// с путём к blob'у (напр. "ФайлПуть" реестра "Документы" ГОСТ-профиля), SizeColumn — необязательная
/// колонка с размером в байтах (напр. "РазмерБайт") — без неё size=0 (влияет только на бейдж
/// отображения, скачивание работает по blobPath независимо).
/// Кодируется строкой <c>@@file:{"column":"ФайлПуть","sizeColumn":"РазмерБайт"}</c>.
/// </summary>
public record DataSetFileMapping(string Column, string? SizeColumn);

public static class DataSetMappingValue
{
    public const string RefPrefix = "@@ref:";
    public const string FilePrefix = "@@file:";

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

    public static bool IsFile(string? value) =>
        value is not null && value.StartsWith(FilePrefix, StringComparison.Ordinal);

    public static DataSetFileMapping? ParseFile(string? value)
    {
        if (!IsFile(value)) return null;
        try
        {
            var json = value![FilePrefix.Length..];
            var parsed = JsonSerializer.Deserialize<DataSetFileMapping>(json, JsonOpts);
            return parsed is null || string.IsNullOrWhiteSpace(parsed.Column) ? null : parsed;
        }
        catch
        {
            return null;
        }
    }

    private static readonly Dictionary<string, string> MimeTypesByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pdf"] = "application/pdf",
        ["docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ["xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ["xls"] = "application/vnd.ms-excel",
        ["png"] = "image/png",
        ["jpg"] = "image/jpeg",
        ["jpeg"] = "image/jpeg",
        ["gif"] = "image/gif",
        ["webp"] = "image/webp",
        ["svg"] = "image/svg+xml",
    };

    /// <summary>
    /// Строит {$type,blobPath,fileName,mimeType,size} из строки источника по колонкам файлового
    /// маппинга. null — колонка с путём к blob'у пуста/отсутствует в строке (поле не заполняется).
    /// </summary>
    public static Dictionary<string, object?>? ResolveFileValue(DataSetFileMapping map, IReadOnlyDictionary<string, string?> row)
    {
        if (!row.TryGetValue(map.Column, out var blobPath) || string.IsNullOrWhiteSpace(blobPath))
            return null;

        var segment = blobPath.Contains('/') ? blobPath[(blobPath.LastIndexOf('/') + 1)..] : blobPath;
        var underscoreIdx = segment.IndexOf('_');
        var fileName = underscoreIdx >= 0 ? segment[(underscoreIdx + 1)..] : segment;
        var ext = System.IO.Path.GetExtension(fileName).TrimStart('.');
        var mimeType = MimeTypesByExtension.GetValueOrDefault(ext, "application/octet-stream");

        long size = 0;
        if (map.SizeColumn is not null && row.TryGetValue(map.SizeColumn, out var sizeStr) && long.TryParse(sizeStr, out var parsedSize))
            size = parsedSize;

        return new Dictionary<string, object?>
        {
            ["$type"] = "file",
            ["blobPath"] = blobPath,
            ["fileName"] = fileName,
            ["mimeType"] = mimeType,
            ["size"] = size,
        };
    }

    // ── Материализация: эффективный маппинг привязки (issue #19/#23) ──────────────

    /// <summary>Маппинг «пустой» (значит, берём материализацию источника): null/пустой объект/все значения пусты.</summary>
    public static bool IsEmptyMapping(string? mappingJson)
    {
        if (string.IsNullOrWhiteSpace(mappingJson)) return true;
        var m = JsonSerializer.Deserialize<Dictionary<string, string>>(mappingJson);
        return m is null || m.Count == 0 || m.Values.All(string.IsNullOrEmpty);
    }

    /// <summary>
    /// Эффективный маппинг привязки: если у привязки нет своего маппинга, а источник материализован —
    /// берём маппинг с источника (тип↔тип). Единый источник истины для резолвера (генерация) и превью.
    /// </summary>
    public static string EffectiveMappingJson(string bindingMapping, Guid? sourceMaterializeTypeId, string? sourceMaterializeMapping)
        => sourceMaterializeTypeId is not null && IsEmptyMapping(bindingMapping)
            ? (sourceMaterializeMapping ?? "{}")
            : bindingMapping;
}
