using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Generation;

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
/// <para>
/// С issue #522 значение бывает ещё и ссылкой на блоб — <c>{ $type:"image", blobPath, width?, ... }</c>.
/// Байты тогда берутся из хранилища, а не из строки; на выходе форма та же, и шаблоны об этом не знают.
/// Дискриминатор именно <c>"image"</c>, а не <c>"file"</c>: узел вложения обслуживает
/// <see cref="TypstFileMaterializer"/>, и его контракт (<c>fileName/mimeType/pageCount</c>) другой —
/// свести их значило бы отнять у поля-картинки <c>width/align/fit</c> и сломать <c>img()</c>.
/// </para>
/// </summary>
public static class TypstImageMaterializer
{
    /// <summary>
    /// Возвращает JSON для data.json, попутно записав изображения в <paramref name="targetDir"/>/<paramref name="assetsSubdir"/>.
    /// </summary>
    public static Task<string> MaterializeAsync(IReadOnlyDictionary<string, object?> data, string targetDir,
        IBlobStorage? blob = null, string assetsSubdir = "assets", JsonSerializerOptions? outputOptions = null,
        CancellationToken ct = default)
        => MaterializeNodeAsync(JsonSerializer.SerializeToNode(data) ?? new JsonObject(),
            targetDir, blob, assetsSubdir, outputOptions, ct);

    /// <summary>То же, но на входе готовый JSON-текст (для отладочного комплекта).</summary>
    public static Task<string> MaterializeJsonAsync(string json, string targetDir,
        IBlobStorage? blob = null, string assetsSubdir = "assets", JsonSerializerOptions? outputOptions = null,
        CancellationToken ct = default)
        => MaterializeNodeAsync(JsonNode.Parse(json) ?? new JsonObject(),
            targetDir, blob, assetsSubdir, outputOptions, ct);

    private static async Task<string> MaterializeNodeAsync(JsonNode root, string targetDir, IBlobStorage? blob,
        string assetsSubdir, JsonSerializerOptions? outputOptions, CancellationToken ct)
    {
        var ctx = new Context(Path.Combine(targetDir, assetsSubdir), assetsSubdir, blob);
        await WalkAsync(root, ctx, ct);
        return outputOptions is null ? root.ToJsonString() : root.ToJsonString(outputOptions);
    }

    private sealed class Context(string assetsDir, string assetsSubdir, IBlobStorage? blob)
    {
        public string AssetsDir { get; } = assetsDir;
        public string AssetsSubdir { get; } = assetsSubdir;
        /// <summary>Хранилище для узлов-ссылок; null — их просто не будет чем прочитать (тесты без блобов).</summary>
        public IBlobStorage? Blob { get; } = blob;
        public int Count;
    }

    private static async Task WalkAsync(JsonNode? node, Context ctx, CancellationToken ct)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                    await ReplaceAsync(ctx, v => obj[key] = v, obj[key], ct);
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var idx = i;
                    await ReplaceAsync(ctx, v => arr[idx] = v, arr[idx], ct);
                }
                break;
        }
    }

    private static async Task ReplaceAsync(Context ctx, Action<JsonNode?> set, JsonNode? child, CancellationToken ct)
    {
        // Голая data-URI строка (легаси / только что загруженная) — без размера.
        if (child is JsonValue val && val.TryGetValue<string>(out var s) && ImageValues.IsDataImage(s))
        {
            var path = WriteImage(s, ctx);
            if (path is not null) set(BuildImageNode(path, null));
            return;
        }
        // Ссылка на блоб {$type:"image", blobPath, width?, ...} — issue #522. Байты берём из
        // хранилища; форма на выходе та же, что и у data-URI, поэтому шаблоны не различают.
        if (child is JsonObject blobNode && ImageValues.TryGetImageBlobPath(blobNode, out var blobPath))
        {
            var mime = blobNode["mimeType"] is JsonValue mv && mv.TryGetValue<string>(out var m) ? m : null;
            var path = await WriteBlobImageAsync(blobPath, mime, ctx, ct);
            if (path is not null) set(BuildImageNode(path, blobNode));
            return; // не спускаемся внутрь — это лист-значение
        }
        // Объект-значение картинки {src, width, ...} — размер из него самого (issue #246).
        if (child is JsonObject obj && ImageValues.TryGetImageObjectSrc(obj, out var src))
        {
            var path = WriteImage(src, ctx);
            if (path is not null) set(BuildImageNode(path, obj));
            return; // не спускаемся внутрь — это лист-значение
        }
        await WalkAsync(child, ctx, ct);
    }

    /// <summary>
    /// Байты картинки из блоб-хранилища в файл каталога компиляции (issue #522). Хранилища нет или
    /// блоб не читается — узел оставляем как есть: генерация не должна падать целиком из-за одной
    /// картинки, а пустой src в шаблоне даст понятную ошибку Typst с именем файла.
    /// </summary>
    private static async Task<string?> WriteBlobImageAsync(
        string blobPath, string? mimeType, Context ctx, CancellationToken ct)
    {
        if (ctx.Blob is null) return null;
        try
        {
            await using var stream = await ctx.Blob.DownloadAsync(blobPath, ct);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);

            Directory.CreateDirectory(ctx.AssetsDir);
            // Расширение — из ЗАЯВЛЕННОГО типа, а путь блоба только запасной вариант (issue #532):
            // путь может оканчиваться чем угодно, а Typst выбирает декодер по расширению файла.
            // «bin» из ExtFor означает «тип незнакомый» — тогда решает путь блоба, и лишь затем png.
            var byMime = mimeType is null ? "bin" : ExtFor(mimeType);
            // Запасной вариант берётся из пользовательской строки, поэтому чистим: буквы и цифры,
            // не длиннее разумного расширения (issue #672). Выхода из каталога тут не было —
            // GetExtension отдаёт хвост ПОСЛЕДНЕГО сегмента, разделитель в него не попадёт. Чистим
            // потому, что имя файла ниже собирается конкатенацией, и в хвост приезжают пробелы,
            // точки, обратные слэши (на Linux это обычный символ имени) и любая длина: слишком
            // длинное имя обрывает запись файла, а catch ниже проглотит отказ — картинка молча
            // исчезнет из PDF.
            //
            // Соседний TypstFileMaterializer.ExtFor чистит тем же способом, но НЕ дословно так же,
            // и расхождения намеренные: там хвост приводится к нижнему регистру и запасное значение
            // «bin», здесь регистр сохраняем (Typst сопоставляет расширение без учёта регистра), а
            // запасное — «png» (issue #532).
            var byPath = new string(Path.GetExtension(blobPath)
                .TrimStart('.').Where(char.IsLetterOrDigit).Take(8).ToArray());
            var ext = byMime != "bin" ? byMime : byPath;
            if (ext.Length == 0) ext = "png";
            var name = $"img_{ctx.Count++}.{ext}";
            await File.WriteAllBytesAsync(Path.Combine(ctx.AssetsDir, name), ms.ToArray(), ct);
            return AssetPath.FromRoot(ctx.AssetsSubdir, name);
        }
        catch (OperationCanceledException)
        {
            throw;   // отмену не глотаем: иначе «отменённая» генерация тихо доедет до PDF без картинок
        }
        catch (Exception)
        {
            return null;
        }
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
        return AssetPath.FromRoot(ctx.AssetsSubdir, name);
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
