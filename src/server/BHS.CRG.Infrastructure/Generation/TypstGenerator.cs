using BHS.CRG.Application.Common;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Documents;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Generation;

public class TypstGenerator(IBlobStorage blob, ILogger<TypstGenerator> log) : IDocumentGenerator
{
    // Имя точки входа — одно на всю систему: сборка (TypeBlockSlug), раскладка на диск, проверка
    // блоков и заглушка в проверке библиотеки обязаны звать один и тот же файл.
    public const string TypeBlocksFileName = TypeBlockSlug.EntrypointName;
    public const string DataFileName = "data.json";
    public const string UserLibFileName = "userlib.typ";
    public const string AssetsSubdir = "assets";
    public const string FontsSubdir = "fonts";

    public async Task<byte[]> GenerateAsync(GenerationRequest request, CancellationToken ct = default)
    {
        if (request.Format != OutputFormat.Pdf)
            throw new NotSupportedException("TypstGenerator supports only PDF format");

        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tmpDir);

        try
        {
            // Каталог — той же записью, что у отладочного пакета (D3): пакет обещает ровно эти файлы.
            var layout = await new TypstCompileDirectory(blob).WriteAsync(request, tmpDir, JsonLayout.Compact, ct);

            // Картинка, которой хранилище не отдало, — отказ с именем поля, и ДО компиляции (ТЗ CORE-37.2):
            // шаблон, выводящий её под условием, собрался бы, и PDF без подписи вышел бы молча.
            if (layout.UnreadableImages.Count > 0)
                throw new UnreadableImagesException(layout.UnreadableImages);

            // Рабочая папка — tmpDir: шаблон видит "typeblocks.typ" без пути, и сам он назван
            // относительно. Короткий формат диагностик — «путь:строка:колонка: error: текст» — чтобы
            // отказ компиляции можно было РАЗОБРАТЬ и назвать пользователю место (issue #1047). Тот
            // же формат и тот же разборщик, что у проверки блоков и библиотеки.
            var psi = TypstProcess.StartInfo(tmpDir,
                "compile", "template.typ", "output.pdf", "--diagnostic-format", "short", "--root", tmpDir);
            if (layout.FontsDir is not null)
            {
                psi.ArgumentList.Add("--font-path");
                psi.ArgumentList.Add(layout.FontsDir);
            }

            var (exitCode, err) = await TypstProcess.RunAsync(psi, ct);
            if (exitCode != 0)
            {
                // Вывод компилятора — в журнал ЦЕЛИКОМ и здесь, а не конвейером ответа: доменный
                // отказ конвейер не логирует (это штатный ответ), и без этой записи точная причина
                // с путями не осталась бы нигде.
                log.LogWarning("Компиляция шаблона не удалась (код {ExitCode}). Вывод Typst:\n{StdErr}",
                    exitCode, err);

                // Разобрали — отказ наш и адресован пользователю. Не разобрали — исключение остаётся
                // чужим: 500 с идентификатором запроса. «Компилятор сказал непонятное» — это про
                // установку, а не про шаблон, и человеку тут исправлять нечего.
                throw TypstCompileFailure.TryDescribe(err, tmpDir)
                      ?? (Exception)new InvalidOperationException($"Typst compilation failed (exit {exitCode}):\n{err}");
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

    public async Task<byte[]> DebugBundleAsync(GenerationRequest request, BundleNotes notes, CancellationToken ct = default)
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "dbgbundle-" + Guid.NewGuid());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var layout = await new TypstCompileDirectory(blob).WriteAsync(request, tmpDir, JsonLayout.Indented, ct);
            return await DebugBundleArchive.PackAsync(tmpDir, layout, notes, ct);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best effort */ }
        }
    }
}
