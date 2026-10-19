using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Infrastructure.Storage;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Толерантный разбор JSONB-группировки источника «Документы»
/// (<see cref="Domain.DataSets.DataSetSource.GostGrouping"/>): читает и новый формат
/// <c>{Groups:[…]}</c>, и legacy <c>{Documents:[{Code,Name,PageIndices}]}</c> — по принципу
/// «читаем старое, дописываем новое при следующей записи», без ретро-миграции БД
/// (<see cref="GostGroupKind.Document"/> = 0 осознанно выбран под дефолт старого формата).
/// Выделено из <c>DataSetPdfRecognitionService</c> в чистый класс ради юнит-покрытия
/// (см. <c>GostGroupingSerializationTests</c>).
/// </summary>
public static class GostGroupingSerialization
{
    /// <summary>
    /// Пути вырезанных PDF документов — этот класс единственный знает, где в разбиении они лежат:
    /// здесь и <see cref="ReadPartPaths" /> рядом. Пользуются ими уборка прежних частей при
    /// переразрезании и резервная копия (issue #1399): знание, разнесённое по разным классам,
    /// разошлось бы при первой смене формы.
    /// </summary>
    public static HashSet<string> PartPaths(GostGroupingData? grouping) =>
        grouping is null
            ? []
            : grouping.Groups.Select(g => g.BlobPath).OfType<string>().Where(p => p.Length > 0).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Те же пути — прямо из JSON разбиения, не разбирая его целиком (ревью PR #1408). Резервной
    /// копии и проверке восстановления нужны только пути, а <see cref="Parse" /> читает каждое
    /// разбиение дважды и поднимает в память всё: распознанные таблицы, поля каждого листа, — у
    /// больших альбомов это мегабайты на набор, и копия делает так по каждому набору.
    ///
    /// <para>Разбиение чужой формы путей не даёт и не роняет: группа-null, <c>Groups: null</c>,
    /// прежний формат <c>{Documents}</c> (частей у него не было) — пустой перечень. Отказ — только
    /// когда разбиение не прочитать вовсе: не JSON или не объект.</para>
    /// </summary>
    /// <returns>Пути частей; null — разбиение не прочитано.</returns>
    public static HashSet<string>? ReadPartPaths(string? json)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (json is null) return paths;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (doc.RootElement.TryGetProperty(nameof(GostGroupingData.Groups), out var groups) &&
                groups.ValueKind == JsonValueKind.Array)
                foreach (var g in groups.EnumerateArray())
                    if (g.ValueKind == JsonValueKind.Object &&
                        g.TryGetProperty(nameof(GostGroupingGroup.BlobPath), out var p) &&
                        p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } path)
                        paths.Add(path);
            return paths;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Пути вложений из строк реестра «Документы» — значения колонки
    /// <see cref="GostGroupingProjection.FilePathColumn" />: генерация берёт путь части ИМЕННО отсюда.
    /// Берётся только точный путь нашего хранилища (<see cref="BlobPathShape.IsExactPath" />): у группы,
    /// которую не удалось вырезать, в колонке может лежать текст штампа (ревью PR #1408). Читают его
    /// резервная копия и редактор разбиения (счёт документов без файла, issue #1534) — одно знание.
    /// </summary>
    /// <returns>Пути; null — строки не прочитаны (не JSON).</returns>
    public static HashSet<string>? ReadRowFilePaths(string? cachedData)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(cachedData)) return paths;
        try
        {
            using var doc = JsonDocument.Parse(cachedData);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return paths;
            foreach (var row in doc.RootElement.EnumerateArray())
                if (row.ValueKind == JsonValueKind.Object &&
                    row.TryGetProperty(GostGroupingProjection.FilePathColumn, out var path) &&
                    path.ValueKind == JsonValueKind.String && path.GetString() is { Length: > 0 } part &&
                    BlobPathShape.IsExactPath(part))
                    paths.Add(part);
            return paths;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>null, если <paramref name="json"/> null (у источника ещё нет группировки).</summary>
    public static GostGroupingData? Parse(string? json)
    {
        if (json is null) return null;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("Groups", out _))
            return JsonSerializer.Deserialize<GostGroupingData>(json); // новый формат

        // Старый формат: Documents:[{Code,Name,PageIndices}].
        var manuallyEdited = root.TryGetProperty("ManuallyEdited", out var me) && me.ValueKind == JsonValueKind.True;
        var groups = new List<GostGroupingGroup>();
        if (root.TryGetProperty("Documents", out var docs) && docs.ValueKind == JsonValueKind.Array)
            foreach (var d in docs.EnumerateArray())
            {
                var code = d.TryGetProperty("Code", out var c) ? c.GetString() : null;
                var name = d.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                var pages = new List<GostGroupingPage>();
                if (d.TryGetProperty("PageIndices", out var pi) && pi.ValueKind == JsonValueKind.Array)
                    foreach (var x in pi.EnumerateArray())
                        pages.Add(new GostGroupingPage(x.GetInt32(), new Dictionary<string, string?>()));
                groups.Add(new GostGroupingGroup(GostGroupKind.Document, code, name, pages));
            }
        return new GostGroupingData(groups, manuallyEdited);
    }
}
