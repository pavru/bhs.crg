using System.Text.Json;
using System.Text.Json.Nodes;

namespace BHS.CRG.Infrastructure.Generation;

/// <summary>
/// Готовит данные для Typst: поля-изображения хранятся как data-URI (<c>data:image/png;base64,...</c>).
/// Typst не умеет их загружать напрямую, поэтому каждое изображение декодируется в файл
/// (<c>assets/img_N.ext</c>) внутри каталога компиляции, а в JSON значение заменяется на объект
/// <c>{ src, width, height, align, fit }</c> (путь к файлу + размер/выравнивание).
/// <para>
/// Размер/выравнивание берутся из САМОГО значения (issue #246): если значение — объект
/// <c>{ src: data-URI, width?, ... }</c>, опции читаются из него; если голая data-URI строка (легаси) —
/// опций нет. Раньше опции задавались в схеме типа и подмешивались по имени поля.
/// </para>
/// В шаблоне такое поле используется через хелпер <c>img(it.Поле)</c> (или <c>image(it.Поле.src)</c>).
/// </summary>
public static class TypstImageMaterializer
{
    /// <summary>
    /// Возвращает JSON для data.json, попутно записав изображения в <paramref name="targetDir"/>/<paramref name="assetsSubdir"/>.
    /// </summary>
    public static string Materialize(IReadOnlyDictionary<string, object?> data, string targetDir,
        string assetsSubdir = "assets", JsonSerializerOptions? outputOptions = null)
        => MaterializeNode(JsonSerializer.SerializeToNode(data) ?? new JsonObject(), targetDir, assetsSubdir, outputOptions);

    /// <summary>То же, но на входе готовый JSON-текст (для отладочного комплекта).</summary>
    public static string MaterializeJson(string json, string targetDir,
        string assetsSubdir = "assets", JsonSerializerOptions? outputOptions = null)
        => MaterializeNode(JsonNode.Parse(json) ?? new JsonObject(), targetDir, assetsSubdir, outputOptions);

    private static string MaterializeNode(JsonNode root, string targetDir, string assetsSubdir,
        JsonSerializerOptions? outputOptions)
    {
        var ctx = new Context(Path.Combine(targetDir, assetsSubdir), assetsSubdir);
        Walk(root, ctx);
        return outputOptions is null ? root.ToJsonString() : root.ToJsonString(outputOptions);
    }

    private sealed class Context(string assetsDir, string assetsSubdir)
    {
        public string AssetsDir { get; } = assetsDir;
        public string AssetsSubdir { get; } = assetsSubdir;
        public int Count;
    }

    private static void Walk(JsonNode? node, Context ctx)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                    Replace(ctx, v => obj[key] = v, obj[key]);
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var idx = i;
                    Replace(ctx, v => arr[idx] = v, arr[idx]);
                }
                break;
        }
    }

    private static void Replace(Context ctx, Action<JsonNode?> set, JsonNode? child)
    {
        // Голая data-URI строка (легаси / только что загруженная) — без размера.
        if (child is JsonValue val && val.TryGetValue<string>(out var s) && ImageValues.IsDataImage(s))
        {
            var path = WriteImage(s, ctx);
            if (path is not null) set(BuildImageNode(path, null));
            return;
        }
        // Объект-значение картинки {src, width, ...} — размер из него самого (issue #246).
        if (child is JsonObject obj && ImageValues.TryGetImageObjectSrc(obj, out var src))
        {
            var path = WriteImage(src, ctx);
            if (path is not null) set(BuildImageNode(path, obj));
            return; // не спускаемся внутрь — это лист-значение
        }
        Walk(child, ctx);
    }

    // Изображение отдаём объектом {src, width, height, align, fit}; размерные ключи — из значения-объекта
    // (null, если не заданы или значение было голой строкой). Форма стабильна для хелпера img() в userlib.
    private static JsonObject BuildImageNode(string path, JsonObject? source)
    {
        string? Opt(string k) => source?[k] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
        return new JsonObject
        {
            ["src"] = path,
            ["width"] = Opt("width"),
            ["height"] = Opt("height"),
            ["align"] = Opt("align"),
            ["fit"] = Opt("fit"),
        };
    }

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
