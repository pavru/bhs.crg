using System.Text.Json.Nodes;
using BHS.CRG.Application.Common;
using PdfSharpCore.Pdf.IO;

namespace BHS.CRG.Infrastructure.Generation;

/// <summary>
/// Материализует поля-вложения контекста (<c>{$type:"file", blobPath, fileName, mimeType, size}</c>)
/// в файлы каталога компиляции: скачивает blob из хранилища и заменяет значение объектом
/// <c>{src, fileName, mimeType, pageCount}</c>, где <c>src</c> — относительный путь к файлу.
/// В шаблоне: <c>image(it.Поле.src)</c> — картинка или 1-я страница PDF; <c>image(src, page: n)</c> —
/// конкретная страница; цикл по <c>pageCount</c> — весь PDF. Typst 0.13+ вставляет страницы PDF
/// нативно, с сохранением текстового слоя.
/// <para>Отличается от <see cref="TypstImageMaterializer"/> (тот синхронный, обрабатывает data-URI
/// картинок в реквизитах): здесь источник — blob-хранилище, поэтому проход асинхронный.</para>
/// </summary>
public sealed class TypstFileMaterializer(IBlobStorage blob)
{
    /// <summary>Потолок буферизации одного вложения — крупнее пропускаем (не роняя генерацию).</summary>
    public const long MaxAttachmentBytes = 100L * 1024 * 1024;

    /// <summary>
    /// Обходит дерево, материализуя file-узлы. <paramref name="place"/> сохраняет байты вложения
    /// (на диск при генерации / в словарь для debug-bundle) и возвращает относительный путь для
    /// <c>src</c>. Имена файлов генерируются приёмником — не из <c>fileName</c> (защита от path traversal).
    /// </summary>
    public async Task MaterializeAsync(JsonNode? root, Func<byte[], string, string> place, CancellationToken ct = default)
        => await WalkAsync(root, place, ct);

    private async Task WalkAsync(JsonNode? node, Func<byte[], string, string> place, CancellationToken ct)
    {
        switch (node)
        {
            case JsonObject obj when TryGetBlobPath(obj, out var blobPath):
                await ReplaceFileNode(obj, blobPath, place, ct);
                break;
            case JsonObject obj:
                foreach (var child in obj.Select(kv => kv.Value).ToList())
                    await WalkAsync(child, place, ct);
                break;
            case JsonArray arr:
                foreach (var child in arr.ToList())
                    await WalkAsync(child, place, ct);
                break;
        }
    }

    private static bool TryGetBlobPath(JsonObject obj, out string blobPath)
    {
        blobPath = "";
        if (obj["$type"]?.GetValue<string>() != "file") return false;
        var bp = obj["blobPath"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(bp)) return false;
        blobPath = bp;
        return true;
    }

    private async Task ReplaceFileNode(JsonObject fileNode, string blobPath,
        Func<byte[], string, string> place, CancellationToken ct)
    {
        var bytes = await TryDownload(blobPath, ct);
        if (bytes is null) return; // отсутствует/слишком большой — оставляем узел как есть, не роняем генерацию

        var fileName = fileNode["fileName"]?.GetValue<string>() ?? "attachment";
        var mimeType = fileNode["mimeType"]?.GetValue<string>() ?? "application/octet-stream";
        var ext = ExtFor(fileName, mimeType);
        var isPdf = ext == "pdf" || mimeType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);

        var src = place(bytes, ext);
        var replacement = new JsonObject
        {
            ["src"] = src,
            ["fileName"] = fileName,
            ["mimeType"] = mimeType,
        };
        if (isPdf && TryPageCount(bytes, out var pages)) replacement["pageCount"] = pages;

        fileNode.ReplaceWith(replacement);
    }

    private async Task<byte[]?> TryDownload(string blobPath, CancellationToken ct)
    {
        try
        {
            await using var stream = await blob.DownloadAsync(blobPath, ct);
            using var buffer = new MemoryStream();
            var pool = new byte[81920];
            int read;
            long total = 0;
            while ((read = await stream.ReadAsync(pool, ct)) > 0)
            {
                total += read;
                if (total > MaxAttachmentBytes) return null; // слишком большое — пропускаем
                buffer.Write(pool, 0, read);
            }
            return buffer.ToArray();
        }
        catch { return null; } // недоступный blob не должен прерывать генерацию документа
    }

    private static bool TryPageCount(byte[] pdfBytes, out int pages)
    {
        pages = 0;
        try
        {
            using var input = new MemoryStream(pdfBytes);
            using var doc = PdfReader.Open(input, PdfDocumentOpenMode.InformationOnly);
            pages = doc.PageCount;
            return pages > 0;
        }
        catch { return false; }
    }

    // Расширение по имени файла, иначе по mime. Санитизируем — только буквы/цифры (имя на диске
    // генерирует приёмник, здесь ext лишь для читаемости и корректной загрузки Typst).
    private static string ExtFor(string fileName, string mimeType)
    {
        var ext = System.IO.Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(ext))
            ext = mimeType.ToLowerInvariant() switch
            {
                "application/pdf" => "pdf",
                "image/png" => "png",
                "image/jpeg" or "image/jpg" => "jpg",
                "image/webp" => "webp",
                "image/gif" => "gif",
                "image/svg+xml" => "svg",
                _ => "bin",
            };
        return new string(ext.Where(char.IsLetterOrDigit).ToArray()) is { Length: > 0 } clean ? clean : "bin";
    }
}
