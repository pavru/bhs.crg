using System.Text.Json;
using BHS.CRG.Application.DataSets;

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
