using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using BHS.CRG.Application.Generation;

namespace BHS.CRG.Infrastructure.Generation;

/// <summary>
/// Синтакс-проверка typeblocks.typ через Typst CLI (issue #309, фаза 2). Пишет typeblocks.typ + harness
/// `check.typ`, который лишь ИМПОРТИРУЕТ его (`#import: *`) — тела-замыкания не вызываются, ленивые
/// семантические ошибки не всплывают; ловятся синтаксические (парсер обходит весь файл). `--diagnostic-format
/// short` даёт разбираемые строки `typeblocks.typ:line:col: error: …`, маппящиеся по line-map на блок.
/// Тот же CLI (env TYPST_PATH) и паттерн запуска процесса, что у TypstGenerator.
/// </summary>
public class TypstSyntaxChecker : ITypstSyntaxChecker
{
    private static readonly string TypstPath =
        Environment.GetEnvironmentVariable("TYPST_PATH") ?? "typst";

    // Короткий формат диагностики: путь:строка:колонка: severity: сообщение.
    private static readonly Regex ShortDiag =
        new(@"typeblocks\.typ:(\d+):(\d+):\s*(error|warning):\s*(.*)", RegexOptions.Compiled);

    public async Task<IReadOnlyList<TypstSyntaxError>> CheckAsync(string typeBlocksContent, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "typst-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmp, "typeblocks.typ"),
                string.IsNullOrEmpty(typeBlocksContent) ? "// empty" : typeBlocksContent, Encoding.UTF8, ct);

            // Harness: импорт форсит ПАРС typeblocks.typ; тела ленивы (не вызваны) → без ложных ошибок
            // данных. Немного текста — чтобы документ имел страницу и Typst не ругался на пустой вывод.
            await File.WriteAllTextAsync(Path.Combine(tmp, "check.typ"),
                "#import \"typeblocks.typ\": *\n" + "x\n", Encoding.UTF8, ct);

            var psi = new ProcessStartInfo
            {
                FileName = TypstPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = tmp,
            };
            foreach (var a in new[] { "compile", "check.typ", "out.pdf", "--diagnostic-format", "short", "--root", tmp })
                psi.ArgumentList.Add(a);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Не удалось запустить Typst CLI");

            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stderr = await stderrTask;

            var errors = new List<TypstSyntaxError>();
            foreach (Match m in ShortDiag.Matches(stderr))
                if (m.Groups[3].Value == "error")
                    errors.Add(new TypstSyntaxError(
                        int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[4].Value.Trim()));
            return errors;
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
        }
    }
}
