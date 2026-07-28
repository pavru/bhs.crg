using System.Diagnostics;
using System.Text;
using BHS.CRG.Application.Generation;
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

            // Рядом с деревом кладём ЗАГЛУШКИ того, что при генерации лежит там же (issue #510).
            // С #506 зонд компилирует и неподключённые файлы — а это ровно те, которые шаблоны
            // импортируют напрямую, и потому чаще прочих обращаются к «/typeblocks.typ», данным или
            // системной библиотеке. Без заглушек такой файл получал бы «file not found» на каждом
            // сохранении и навсегда садился под жёлтую полосу «не собирается», хотя генерируется он
            // прекрасно. Остаётся честный остаток: чтение ПОЛЕЙ данных на верхнем уровне упрётся в
            // пустой объект — настоящих данных у проверки нет и быть не может.
            await File.WriteAllTextAsync(
                Path.Combine(tmp, TypstGenerator.TypeBlocksFileName), string.Empty, Encoding.UTF8, ct);
            await File.WriteAllTextAsync(
                Path.Combine(tmp, TypstGenerator.DataFileName), "{}", Encoding.UTF8, ct);
            await File.WriteAllTextAsync(
                Path.Combine(tmp, SystemTypstLib.FileName), SystemTypstLib.Content, Encoding.UTF8, ct);
            Directory.CreateDirectory(Path.Combine(tmp, TypstGenerator.AssetsSubdir));

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
            await File.WriteAllTextAsync(Path.Combine(tmp, UserLibAnalysis.ProbeName), probe.ToString(), Encoding.UTF8, ct);

            var psi = new ProcessStartInfo
            {
                FileName = TypstPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = tmp,
            };
            foreach (var a in new[] { "compile", UserLibAnalysis.ProbeName, "out.pdf", "--diagnostic-format", "short", "--root", tmp })
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

            var errors = TypstShortDiagnostics.Parse(stderr)
                .Where(d => d.Severity == "error")
                // Ошибки самого зонда пользователю не показать — он его не писал и не увидит
                // (issue #509). Отбираем по ИМЕНИ среди путей, не приводимых к дереву: «userlib/
                // check.typ» — законное имя файла дерева, но оно приводится и сюда не попадает.
                // Сравнение с путём временной папки, стоявшее здесь до того, зависело от того, как
                // хост канонизирует эту папку (короткие имена 8.3, «/private/var/…»), и молча
                // переставало совпадать — а с #508 непривязанный путь считается входящим в сборку,
                // так что ошибка зонда приезжала бы красной полосой «генерация не пройдёт».
                .Where(d => !(UserLibAnalysis.ToLibPath(d.File) is null
                    && UserLibAnalysis.IsProbePath(d.File, Path.GetFileName(tmp))))
                .Select(d =>
                {
                    // Путь, который не удалось привести к дереву (диагностика из файла пакета
                    // @preview, из служебного файла генерации), считаем ВХОДЯЩИМ в сборку —
                    // issue #508. Обратное умолчание давало худший исход: неизвестное показывалось
                    // бы мягкой жёлтой полосой «в сборку не входит», а Ok оставался бы true, то есть
                    // при сломанном пакете админу сообщали бы, что библиотека собирается, тогда как
                    // встала генерация всех документов. Молчаливое зелёное — ровно то, против чего
                    // вся эта проверка.
                    var mapped = UserLibAnalysis.ToLibPath(d.File);
                    var path = mapped ?? d.File;
                    var inBuild = mapped is null
                        || path == UserLibAnalysis.EntrypointName
                        || reachable.Contains(path);
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

}
