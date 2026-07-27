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
            await File.WriteAllTextAsync(Path.Combine(tmp, "check.typ"),
                $"#import \"{UserLibAnalysis.EntrypointName}\": *\nx\n", Encoding.UTF8, ct);

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

            var errors = TypstShortDiagnostics.Parse(stderr)
                .Where(d => d.Severity == "error")
                .Select(d => new UserLibError(ToLibPath(d.File), d.Line, d.Column, d.Message))
                // Ошибки самого зонда пользователю не показать — он его не писал и не увидит.
                .Where(e => e.Path != "check.typ")
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
