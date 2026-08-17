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

            var known = (files ?? []).Select(f => f.Path).ToList();
            var errors = new Dictionary<(string File, int Line, int Column, string Message), TypstSyntaxError>();

            // Первый заход — через агрегатор: он импортирует все модули, и одного запуска хватает,
            // когда всё в порядке (обычный случай).
            foreach (var e in await RunProbeAsync(tmp, TypeBlockSlug.EntrypointName, known, ct))
                errors.TryAdd((e.File, e.Line, e.Column, e.Message), e);

            // Если что-то сломано — добираем остальные модули поштучно. Typst останавливает
            // вычисление на ПЕРВОМ неудачном `#import`, поэтому ошибки следующих модулей в общий
            // прогон не попадают: во flat-файле все они приходили разом (парсер обходил его целиком),
            // и без этого прохода админ с тремя сломанными типами чинил бы их по одному, каждый раз
            // заново нажимая «Проверить». Медленный путь включается только когда уже есть поломка.
            if (errors.Count > 0)
                foreach (var module in known.Where(p => p != TypeBlockSlug.EntrypointName))
                    foreach (var e in await RunProbeAsync(tmp, module, known, ct))
                        errors.TryAdd((e.File, e.Line, e.Column, e.Message), e);

            return errors.Values.ToList();
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Один прогон CLI: harness импортирует <paramref name="target"/> — импорт форсит ПАРС файла (и
    /// всего, что он тянет), а тела-замыкания не вызываются, поэтому ленивые семантические ошибки не
    /// всплывают. Немного текста — чтобы документ имел страницу и Typst не ругался на пустой вывод.
    /// </summary>
    private static async Task<IReadOnlyList<TypstSyntaxError>> RunProbeAsync(
        string tmp, string target, IReadOnlyList<string> known, CancellationToken ct)
    {
        await File.WriteAllTextAsync(Path.Combine(tmp, ProbeName),
            $"#import \"{target}\": *\n" + "x\n", Encoding.UTF8, ct);

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
}
