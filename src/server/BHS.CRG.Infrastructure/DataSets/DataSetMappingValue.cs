using System.Text.Json;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Значение маппинга колонки-ссылки на запись каталога (составное поле). Два вида резолва «строка→объект»
/// (issue #243, сходимость к решению #183 — только Name или IdentityKey):
///  • <b>по имени</b>: <c>@@ref:{"strategy":"Name","column":"Наименование","typeId":"&lt;guid&gt;"}</c> —
///    матч по DisplayName ∪ Aliases значения колонки;
///  • <b>по идентификатору</b>: <c>@@ref:{"strategy":"Identity","identityColumns":{"ИНН":"КолонкаИНН",
///    "КПП":"КолонкаКПП"},"typeId":"&lt;guid&gt;"}</c> — матч по составному ключу identity-полей типа
///    (по колонке на каждое identity-поле).
/// <b>Legacy</b> (создание через UI больше не даётся, но старые маппинги читаются вечно):
/// <c>{"column":"ИНН","match":"ИНН","typeId":…}</c> — непустой <c>match</c> = стратегия Field (произвольное
/// поле), пустой = Name. Формат разделяется с фронтендом (MappingEditor).
/// </summary>
public record DataSetRefMapping(
    string? Column,
    string? Match,
    Guid TypeId,
    string? Strategy = null,
    Dictionary<string, string>? IdentityColumns = null)
{
    /// <summary>Резолв по составному identity-ключу (новый формат): задан IdentityColumns.</summary>
    public bool IsIdentity => string.Equals(Strategy, "Identity", StringComparison.OrdinalIgnoreCase)
        && IdentityColumns is { Count: > 0 };
}

/// <summary>
/// Файловый маппинг — поле типа "file" заполняется вложением, синтезированным из колонок ТОЙ ЖЕ
/// строки источника (в отличие от ref-маппинга — здесь нет cross-table lookup). Column — колонка
/// с путём к blob'у (напр. "ФайлПуть" реестра "Документы" ГОСТ-профиля), SizeColumn — необязательная
/// колонка с размером в байтах (напр. "РазмерБайт") — без неё size=0 (влияет только на бейдж
/// отображения, скачивание работает по blobPath независимо).
/// Кодируется строкой <c>@@file:{"column":"ФайлПуть","sizeColumn":"РазмерБайт"}</c>.
/// </summary>
public record DataSetFileMapping(string Column, string? SizeColumn);

/// <summary>
/// Inline-маппинг составного поля (issue #374): значение поля собирается КАК ВСТРОЕННЫЙ ОБЪЕКТ из
/// колонок ТОЙ ЖЕ строки (в отличие от <see cref="DataSetRefMapping"/> — без cross-table lookup).
/// <see cref="Fields"/> — под-поле → токен (та же грамматика маппинга: имя колонки / <c>@@ref:…</c> /
/// вложенный <c>@@inline:…</c>) — рекурсивно. <see cref="TypeId"/> — объявленный составной тип поля.
/// Кодируется строкой <c>@@inline:{"typeId":"&lt;guid&gt;","fields":{"Подполе":"Колонка",…}}</c>.
/// </summary>
public record DataSetInlineMapping(Guid TypeId, Dictionary<string, string> Fields);

public static class DataSetMappingValue
{
    public const string RefPrefix = "@@ref:";
    public const string FilePrefix = "@@file:";
    public const string InlinePrefix = "@@inline:";

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
            // Валиден, если задан typeId И есть чем резолвить: колонка (Name/legacy) ИЛИ identityColumns (Identity).
            var noSource = string.IsNullOrWhiteSpace(parsed?.Column)
                && (parsed?.IdentityColumns is null || parsed.IdentityColumns.Count == 0);
            return parsed is null || parsed.TypeId == Guid.Empty || noSource ? null : parsed;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsInline(string? value) =>
        value is not null && value.StartsWith(InlinePrefix, StringComparison.Ordinal);

    public static DataSetInlineMapping? ParseInline(string? value)
    {
        if (!IsInline(value)) return null;
        try
        {
            var json = value![InlinePrefix.Length..];
            var parsed = JsonSerializer.Deserialize<DataSetInlineMapping>(json, JsonOpts);
            // Валиден, если задан хотя бы один под-маппинг (иначе строить нечего).
            return parsed is null || parsed.Fields is null || parsed.Fields.Count == 0 ? null : parsed;
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
