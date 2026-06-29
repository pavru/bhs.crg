using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Application.Schema;

namespace BHS.CRG.Infrastructure.Generation;

/// <summary>
/// Готовит данные для Typst: поля-изображения хранятся как data-URI
/// (<c>data:image/png;base64,...</c>). Typst не умеет их загружать напрямую,
/// поэтому каждое изображение декодируется в файл (<c>assets/img_N.ext</c>) внутри
/// каталога компиляции, а в JSON значение заменяется на относительный путь к файлу.
/// В шаблоне такое поле используется как <c>image(it.Поле)</c>.
/// </summary>
public static class TypstImageMaterializer
{
    /// <summary>
    /// Возвращает JSON для data.json, попутно записав изображения в <paramref name="targetDir"/>/<paramref name="assetsSubdir"/>.
    /// </summary>
    public static string Materialize(IReadOnlyDictionary<string, object?> data, string targetDir,
        string assetsSubdir = "assets", JsonSerializerOptions? outputOptions = null,
        IReadOnlyDictionary<string, ImageRenderOptions>? imageOptions = null)
        => MaterializeNode(JsonSerializer.SerializeToNode(data) ?? new JsonObject(), targetDir, assetsSubdir, outputOptions, imageOptions);

    /// <summary>То же, но на входе готовый JSON-текст (для отладочного комплекта).</summary>
    public static string MaterializeJson(string json, string targetDir,
        string assetsSubdir = "assets", JsonSerializerOptions? outputOptions = null,
        IReadOnlyDictionary<string, ImageRenderOptions>? imageOptions = null)
        => MaterializeNode(JsonNode.Parse(json) ?? new JsonObject(), targetDir, assetsSubdir, outputOptions, imageOptions);

    private static string MaterializeNode(JsonNode root, string targetDir, string assetsSubdir,
        JsonSerializerOptions? outputOptions, IReadOnlyDictionary<string, ImageRenderOptions>? imageOptions)
    {
        var ctx = new Context(Path.Combine(targetDir, assetsSubdir), assetsSubdir, imageOptions);
        Walk(root, ctx);
        return outputOptions is null ? root.ToJsonString() : root.ToJsonString(outputOptions);
    }

    private sealed class Context(string assetsDir, string assetsSubdir, IReadOnlyDictionary<string, ImageRenderOptions>? options)
    {
        public string AssetsDir { get; } = assetsDir;
        public string AssetsSubdir { get; } = assetsSubdir;
        public IReadOnlyDictionary<string, ImageRenderOptions>? Options { get; } = options;
        public int Count;
    }

    private static void Walk(JsonNode? node, Context ctx)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                    Replace(ctx, key, v => obj[key] = v, obj[key]);
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var idx = i;
                    Replace(ctx, null, v => arr[idx] = v, arr[idx]);
                }
                break;
        }
    }

    private static void Replace(Context ctx, string? propertyKey, Action<JsonNode?> set, JsonNode? child)
    {
        if (child is JsonValue val && val.TryGetValue<string>(out var s) && IsDataImage(s))
        {
            var path = WriteImage(s, ctx);
            if (path is not null) set(BuildImageNode(path, propertyKey, ctx));
        }
        else
        {
            Walk(child, ctx);
        }
    }

    // Изображение отдаём объектом {src, width, height, align, fit} — опции из схемы (по имени поля).
    private static JsonObject BuildImageNode(string path, string? propertyKey, Context ctx)
    {
        ImageRenderOptions? o = null;
        if (propertyKey is not null) ctx.Options?.TryGetValue(propertyKey, out o);
        return new JsonObject
        {
            ["src"] = path,
            ["width"] = o?.Width,
            ["height"] = o?.Height,
            ["align"] = o?.Align,
            ["fit"] = o?.Fit,
        };
    }

    private static bool IsDataImage(string s) =>
        s.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) &&
        s.Contains(";base64,", StringComparison.OrdinalIgnoreCase);

    private static string? WriteImage(string dataUri, Context ctx)
    {
        var sep = dataUri.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
        if (sep < 0) return null;

        var mime = dataUri[5..sep]; // после "data:"
        var base64 = dataUri[(sep + ";base64,".Length)..];

        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { return null; } // не валидный base64 — оставляем как есть

        Directory.CreateDirectory(ctx.AssetsDir);
        var name = $"img_{ctx.Count++}.{ExtFor(mime)}";
        File.WriteAllBytes(Path.Combine(ctx.AssetsDir, name), bytes);
        return $"{ctx.AssetsSubdir}/{name}";
    }

    private static string ExtFor(string mime) => mime.ToLowerInvariant() switch
    {
        "image/png" => "png",
        "image/jpeg" or "image/jpg" => "jpg",
        "image/webp" => "webp",
        "image/gif" => "gif",
        "image/svg+xml" => "svg",
        _ => "bin",
    };
}
