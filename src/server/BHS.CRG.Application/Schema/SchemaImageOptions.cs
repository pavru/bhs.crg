using System.Text.Json;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Schema;

/// <summary>Опции отображения поля-изображения, заданные в схеме (fields[].image).</summary>
public record ImageRenderOptions(string? Width, string? Height, string? Align, string? Fit);

/// <summary>
/// Собирает опции изображений из схем всех типов: fieldKey → опции.
/// Используется генератором, чтобы отдать изображение объектом {src, width, height, align, fit}.
/// Ключ — имя поля (image-поля обычно уникальны; коллизии маловероятны и косметичны).
/// </summary>
public static class SchemaImageOptions
{
    public static IReadOnlyDictionary<string, ImageRenderOptions> Collect(IEnumerable<DocumentType> allDocTypes)
    {
        var map = new Dictionary<string, ImageRenderOptions>(StringComparer.Ordinal);
        foreach (var dt in allDocTypes)
        {
            if (dt.Schema.RootElement.ValueKind != JsonValueKind.Object
                || !dt.Schema.RootElement.TryGetProperty("fields", out var fields)
                || fields.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var f in fields.EnumerateArray())
            {
                if (f.GetPropertyOrNull("type")?.GetString() != "image") continue;
                if (f.GetPropertyOrNull("key")?.GetString() is not { Length: > 0 } key) continue;
                var opts = ParseImage(f);
                if (opts is not null) map[key] = opts;
            }
        }
        return map;
    }

    private static ImageRenderOptions? ParseImage(JsonElement field)
    {
        if (!field.TryGetProperty("image", out var img) || img.ValueKind != JsonValueKind.Object)
            return null;
        string? S(string n) => img.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
        var o = new ImageRenderOptions(S("width"), S("height"), S("align"), S("fit"));
        return (o.Width ?? o.Height ?? o.Align ?? o.Fit) is null ? null : o;
    }

    private static JsonElement? GetPropertyOrNull(this JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) ? v : null;
}
