using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Infrastructure.Generation;

public class TypstGenerator(IBlobStorage blob) : IDocumentGenerator
{
    public const string TypeBlocksFileName = "typeblocks.typ";
    public const string UserLibFileName = "userlib.typ";
    public const string AssetsSubdir = "assets";
    public const string FontsSubdir = "fonts";

    private static readonly string TypstPath =
        Environment.GetEnvironmentVariable("TYPST_PATH") ?? "typst";

    public async Task<byte[]> GenerateAsync(GenerationRequest request, CancellationToken ct = default)
    {
        if (request.Format != OutputFormat.Pdf)
            throw new NotSupportedException("TypstGenerator supports only PDF format");

        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tmpDir);

        try
        {
            // Поля-изображения (data-URI) декодируем в файлы assets/ и подставляем пути,
            // чтобы шаблон мог обращаться к ним через image(it.Поле). Размер/выравнивание — из самого
            // значения-объекта {src, width, ...} (issue #246).
            var dataJson = TypstImageMaterializer.Materialize(request.Context.Data, tmpDir);

            // Поля-вложения ({$type:"file"}) скачиваем из blob-хранилища в assets/ и подставляем путь+
            // pageCount, чтобы шаблон вставлял их через image(it.Поле.src[, page: n]) — в т.ч. страницы PDF.
            var node = JsonNode.Parse(dataJson) ?? new JsonObject();
            var assetsDir = Path.Combine(tmpDir, AssetsSubdir);
            var attCount = 0;
            await new TypstFileMaterializer(blob).MaterializeAsync(node, (bytes, ext) =>
            {
                Directory.CreateDirectory(assetsDir);
                var name = $"att_{attCount++}.{ext}";
                File.WriteAllBytes(Path.Combine(assetsDir, name), bytes);
                return $"{AssetsSubdir}/{name}";
            }, ct);
            dataJson = node.ToJsonString();

            await File.WriteAllTextAsync(Path.Combine(tmpDir, "data.json"), dataJson, ct);

            // template.typ пишется ДОСЛОВНО (issue #353): стандартные импорты живут в самом шаблоне
            // (добавляются при его создании), компиляция их не подставляет → номера строк ошибок = строки редактора.
            await File.WriteAllTextAsync(
                Path.Combine(tmpDir, "template.typ"),
                request.TemplateContent, Encoding.UTF8, ct);

            // systemlib.typ — системная библиотека (хардкод, issue #344). Всегда присутствует в tmpDir,
            // чтобы `#import "systemlib.typ"` в шаблоне резолвился.
            await File.WriteAllTextAsync(Path.Combine(tmpDir, SystemTypstLib.FileName), SystemTypstLib.Content, Encoding.UTF8, ct);

            // Всегда записываем typeblocks.typ — даже пустым.
            // Шаблон импортирует его напрямую: #import "typeblocks.typ": *
            var typeBlocksPath = Path.Combine(tmpDir, TypeBlocksFileName);
            var typeBlocksContent = string.IsNullOrEmpty(request.TypeBlocksContent)
                ? "// no composite-type render functions defined"
                : request.TypeBlocksContent;
            await File.WriteAllTextAsync(typeBlocksPath, typeBlocksContent, Encoding.UTF8, ct);

            if (!File.Exists(typeBlocksPath))
                throw new InvalidOperationException($"Failed to write {TypeBlocksFileName} to {tmpDir}");

            // userlib.typ — пользовательские вспомогательные функции; всегда присутствует в tmpDir
            var userLibContent = string.IsNullOrEmpty(request.UserLibContent)
                ? "// user typst library is empty"
                : request.UserLibContent;
            await File.WriteAllTextAsync(Path.Combine(tmpDir, UserLibFileName), userLibContent, Encoding.UTF8, ct);

            // Ассеты шаблона (issue #62) — уже свёрнутые по приоритету Template>DocumentType>System
            // резолвером (ITemplateAssetResolver). Картинки — в assets/ по стабильному Name (шаблон
            // обращается через image("assets/{Name}.{ext}")); шрифты — в отдельную fonts/, путь к
            // которой передаётся компилятору через --font-path (сам файл на диске может называться
            // как угодно — Typst резолвит шрифт по имени семейства, зашитому в файл, не по filename).
            string? fontsDirForCli = null;
            if (request.TemplateAssets is { } assets)
            {
                foreach (var img in assets.Images)
                {
                    var bytes = await TryDownloadAsync(img.BlobPath, ct);
                    if (bytes is null) continue;
                    Directory.CreateDirectory(assetsDir);
                    var ext = Path.GetExtension(img.FileName);
                    await File.WriteAllBytesAsync(Path.Combine(assetsDir, $"{img.Name}{ext}"), bytes, ct);
                }
                if (assets.Fonts.Count > 0)
                {
                    var fontsDir = Path.Combine(tmpDir, FontsSubdir);
                    Directory.CreateDirectory(fontsDir);
                    var fontIndex = 0;
                    foreach (var font in assets.Fonts)
                    {
                        var bytes = await TryDownloadAsync(font.BlobPath, ct);
                        if (bytes is null) continue;
                        var ext = Path.GetExtension(font.FileName);
                        await File.WriteAllBytesAsync(Path.Combine(fontsDir, $"font_{fontIndex++}{ext}"), bytes, ct);
                    }
                    if (fontIndex > 0) fontsDirForCli = fontsDir;
                }
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = TypstPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = tmpDir,  // шаблон видит "typeblocks.typ" без пути
            };
            psi.ArgumentList.Add("compile");
            psi.ArgumentList.Add("template.typ");   // относительный путь — tmpDir уже WorkingDirectory
            psi.ArgumentList.Add("output.pdf");
            psi.ArgumentList.Add("--root");
            psi.ArgumentList.Add(tmpDir);
            if (fontsDirForCli is not null)
            {
                psi.ArgumentList.Add("--font-path");
                psi.ArgumentList.Add(fontsDirForCli);
            }

            using var process = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start Typst");

            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                var err = await stderrTask;
                throw new InvalidOperationException($"Typst compilation failed (exit {process.ExitCode}):\n{err}");
            }

            var outputPath = Path.Combine(tmpDir, "output.pdf");
            if (!File.Exists(outputPath))
                throw new InvalidOperationException("Typst did not produce output.pdf");

            return await File.ReadAllBytesAsync(outputPath, ct);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private const long MaxAssetBytes = 100L * 1024 * 1024;

    // Скачивание ассета шаблона из blob — тот же толерантный паттерн, что и
    // TypstFileMaterializer.TryDownload (отсутствие/превышение размера — не критично, просто
    // пропускаем конкретный ассет, не прерываем генерацию).
    private async Task<byte[]?> TryDownloadAsync(string blobPath, CancellationToken ct)
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
                if (total > MaxAssetBytes) return null;
                buffer.Write(pool, 0, read);
            }
            return buffer.ToArray();
        }
        catch { return null; }
    }
}
