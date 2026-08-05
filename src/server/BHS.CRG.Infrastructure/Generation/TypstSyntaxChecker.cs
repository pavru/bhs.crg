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

    private const string FileName = "typeblocks.typ";

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

            // Проверка синтаксиса идёт по нажатию в редакторе — срок короче генеративного:
            // блок, который не разбирается за десять секунд, всё равно не годится в шаблон.
            var (_, stderr) = await TypstProcess.RunAsync(psi, ct, TimeSpan.FromSeconds(10));

            // Разбор общий с проверкой библиотеки (issue #473); отбор по имени файла — здесь, а не в
            // самом шаблоне: координаты этого контракта заявлены ВНУТРИ typeblocks.typ, и ошибка из
            // harness'а с чужими номерами строк уехала бы по line-map не туда.
            return TypstShortDiagnostics.Parse(stderr)
                .Where(d => d.Severity == "error" && d.File.EndsWith(FileName, StringComparison.Ordinal))
                .Select(d => new TypstSyntaxError(d.Line, d.Column, d.Message))
                .ToList();
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
        }
    }
}
