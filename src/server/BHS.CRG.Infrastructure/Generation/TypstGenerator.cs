using System.Text;
using System.Text.Json;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Infrastructure.Generation;

public class TypstGenerator : IDocumentGenerator
{
    public const string TypeBlocksFileName = "typeblocks.typ";
    public const string UserLibFileName = "userlib.typ";

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
            // чтобы шаблон мог обращаться к ним через image(it.Поле).
            var dataJson = TypstImageMaterializer.Materialize(request.Context.Data, tmpDir,
                imageOptions: request.ImageOptions);
            await File.WriteAllTextAsync(Path.Combine(tmpDir, "data.json"), dataJson, ct);

            await File.WriteAllTextAsync(
                Path.Combine(tmpDir, "template.typ"),
                request.TemplateContent, Encoding.UTF8, ct);

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
}
