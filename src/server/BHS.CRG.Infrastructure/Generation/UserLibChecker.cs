using System.Diagnostics;
using System.Text;
using BHS.CRG.Application.Templates;

namespace BHS.CRG.Infrastructure.Generation;

/// <summary>
/// Проверка дерева библиотеки Typst (issue #473): раскладываем точку входа и дерево ровно так же, как
/// при генерации, и компилируем зонд, который лишь ИМПОРТИРУЕТ точку входа.
///
/// Зачем вообще: библиотеку импортирует КАЖДЫЙ шаблон, поэтому один сломанный файл останавливает
/// генерацию всех документов. Проверка на сохранении переводит это из «узнаем при следующей
/// генерации» в «видно сразу».
///
/// Чего проверка НЕ делает: тела функций — замыкания, они не вызываются, поэтому ловятся синтаксис и
/// битые пути импортов, но не поведение. Формулировать результат как «библиотека собирается», не как
/// «шаблоны работают».
/// </summary>
public class UserLibChecker : IUserLibChecker
{
    private static readonly string TypstPath =
        Environment.GetEnvironmentVariable("TYPST_PATH") ?? "typst";

    public async Task<UserLibCheckResult> CheckAsync(
        string entrypointContent, IReadOnlyList<UserLibFile> files, CancellationToken ct)
    {
        // Разбор дерева не зависит от компилятора и обязан отработать, даже если CLI недоступен.
        var warnings = UserLibAnalysis.Warnings(entrypointContent, files);

        var tmp = Path.Combine(Path.GetTempPath(), "userlib-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            await UserLibMaterializer.WriteAsync(tmp, entrypointContent, files, ct);

            // Зонд импортирует точку входа так же, как это делает шаблон. Строка текста — чтобы у
            // документа была страница и Typst не ругался на пустой вывод.
            //
            // И отдельно — КАЖДЫЙ файл дерева (issue #506). Typst разбирает только то, до чего дошёл
            // по импортам, поэтому неподключённый файл вообще не проверялся: панель говорила «всё в
            // порядке», а шаблон, импортирующий этот файл напрямую (так делает один из наших), падал
            // при генерации. Про НЕПОДКЛЮЧЁННОСТЬ мы намеренно молчим (#494) — это обычный ход
            // работы; но про сломанный файл молчать нельзя, правило прежнее: говорим о том, что
            // сломано. Импорт с псевдонимом, а не «: *», — чтобы зонд не создавал столкновений имён,
            // которых в настоящей генерации нет.
            var probe = new StringBuilder($"#import \"{UserLibAnalysis.EntrypointName}\": *\n");
            for (var i = 0; i < files.Count; i++)
                probe.Append($"#import \"{UserLibPath.FolderName}/{files[i].Path}\" as _probe{i}\n");
            probe.Append("x\n");
            await File.WriteAllTextAsync(Path.Combine(tmp, "check.typ"), probe.ToString(), Encoding.UTF8, ct);

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

            // Помечаем, входит ли файл в сборку: ошибка в подключённом останавливает генерацию ВСЕХ
            // документов, а в неподключённом — только шаблонов, импортирующих его напрямую. Сказать
            // «генерация не пройдёт» про второй значило бы солгать.
            var reachable = UserLibAnalysis.ReachableFrom(entrypointContent, files);

            // Ошибки самого зонда пользователю не показать — он его не писал и не увидит. Сравнение
            // ПОЛНЫМ путём, а не по имени (issue #507): Typst печатает абсолютный путь, поэтому
            // прежнее «Path != "check.typ"» не совпадало никогда и фильтр был мёртвым — а зонд
            // подрос до строки на каждый файл дерева, и его ошибка приезжала бы админу сырым путём
            // во временную папку, вдобавок помеченная «в сборку не входит». По имени сравнивать
            // нельзя: «userlib/check.typ» — законное имя файла дерева, и его ошибки мы бы съели.
            var probePath = tmp.Replace('\\', '/').TrimEnd('/') + "/check.typ";

            var errors = TypstShortDiagnostics.Parse(stderr)
                .Where(d => d.Severity == "error" && !d.File.EndsWith(probePath, StringComparison.Ordinal))
                .Select(d =>
                {
                    var path = ToLibPath(d.File);
                    var inBuild = path == UserLibAnalysis.EntrypointName || reachable.Contains(path);
                    return new UserLibError(path, d.Line, d.Column, d.Message, inBuild);
                })
                .ToList();

            return new UserLibCheckResult(errors, warnings);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Путь из диагностики — в путь, которым оперирует интерфейс: точка входа как есть, файлы дерева
    /// без префикса <c>userlib/</c> (префикс постоянный и ничего не сообщает).
    /// </summary>
    private static string ToLibPath(string file)
    {
        var prefix = UserLibPath.FolderName + "/";
        var idx = file.IndexOf(prefix, StringComparison.Ordinal);
        if (idx >= 0) return file[(idx + prefix.Length)..];
        return file.EndsWith(UserLibAnalysis.EntrypointName, StringComparison.Ordinal)
            ? UserLibAnalysis.EntrypointName
            : file;
    }
}
