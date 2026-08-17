using System.Diagnostics;
using System.Text;
using BHS.CRG.Application.Generation;

namespace BHS.CRG.Infrastructure.Generation;

/// <summary>
/// Синтакс-проверка блоков типов через Typst CLI (issue #309, фаза 2). Раскладывает набор файлов той
/// же <see cref="TypeBlocksMaterializer"/>, что и генерация, и компилирует harness `check.typ`,
/// который лишь ИМПОРТИРУЕТ агрегатор (`#import: *`) — тела-замыкания не вызываются, ленивые
/// семантические ошибки не всплывают; ловятся синтаксические (парсер обходит весь файл).
/// `--diagnostic-format short` даёт разбираемые строки, которые маппятся на файл и строку блока.
/// Тот же CLI (env TYPST_PATH) и паттерн запуска процесса, что у TypstGenerator.
/// </summary>
public class TypstSyntaxChecker : ITypstSyntaxChecker
{
    private static readonly string TypstPath =
        Environment.GetEnvironmentVariable("TYPST_PATH") ?? "typst";

    private const string ProbeName = "check.typ";

    public async Task<IReadOnlyList<TypstSyntaxError>> CheckAsync(
        IReadOnlyList<TypstBlockFile> files, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "typst-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            await TypeBlocksMaterializer.WriteAsync(tmp, files, ct);

            // Harness: импорт форсит ПАРС агрегатора и всех модулей по цепочке импортов; тела ленивы
            // (не вызваны) → без ложных ошибок данных. Немного текста — чтобы документ имел страницу
            // и Typst не ругался на пустой вывод.
            await File.WriteAllTextAsync(Path.Combine(tmp, ProbeName),
                $"#import \"{TypeBlockSlug.EntrypointName}\": *\n" + "x\n", Encoding.UTF8, ct);

            var psi = new ProcessStartInfo
            {
                FileName = TypstPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = tmp,
            };
            foreach (var a in new[] { "compile", ProbeName, "out.pdf", "--diagnostic-format", "short", "--root", tmp })
                psi.ArgumentList.Add(a);

            // Проверка синтаксиса идёт по нажатию в редакторе — срок короче генеративного:
            // блок, который не разбирается за десять секунд, всё равно не годится в шаблон.
            var (_, stderr) = await TypstProcess.RunAsync(psi, ct, TimeSpan.FromSeconds(10));

            // Привязка диагностики к нашему файлу — по СУФФИКСУ пути: Typst печатает абсолютный путь
            // (на Windows ещё и с префиксом «\\?\»), а приложение оперирует относительными. Отбор по
            // одному имени «typeblocks.typ», стоявший здесь до раскола, после него не совпал бы ни с
            // одним модулем — и проверка вернула бы пустой зелёный список вместо ошибок.
            var known = (files ?? []).Select(f => f.Path).ToList();
            var result = new List<TypstSyntaxError>();
            foreach (var d in TypstShortDiagnostics.Parse(stderr).Where(d => d.Severity == "error"))
            {
                var match = known.FirstOrDefault(p => d.File == p || d.File.EndsWith("/" + p, StringComparison.Ordinal));
                if (match is null)
                {
                    // Ошибки самого harness'а пользователю не показать — он его не писал. Всё
                    // остальное непривязанное пропускаем наверх с путём как есть: молчать о
                    // непонятной ошибке хуже, чем показать её без адреса.
                    if (d.File.EndsWith("/" + ProbeName, StringComparison.Ordinal) || d.File == ProbeName) continue;
                    result.Add(new TypstSyntaxError(d.File, d.Line, d.Column, d.Message));
                    continue;
                }
                result.Add(new TypstSyntaxError(match, d.Line, d.Column, d.Message));
            }
            return result;
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
        }
    }
}
