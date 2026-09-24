using System.Text.Json;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Храповик размера файлов (issue #1041): файлу из базового уровня расти нельзя, новому — рождаться
/// выше порога тоже.
///
/// <para>Зачем. Файл-склад читают ЦЕЛИКОМ ради одной правки — и человек, и ревью, и агент. За день
/// 24.09.2026 из репозитория ушли восемь таких (#1014, #1021, #1022, #1029, #1030, #1031, #1032,
/// #1033), и каждый разрез стоил полного прогона тестов, живого прогона и цикла CI. Дешевле не дать
/// им вырасти заново: следующий склад обходится дороже этого сторожа на порядок.</para>
///
/// <para>Приём — тот же, что у храповика линта (#854): накопленное чинить не требуется, требуется
/// не добавлять. Сравнение пофайловое, а не по общей сумме, иначе «разрезал один, раздул другой»
/// прошло бы молча.</para>
///
/// <para>⚠️ Считаются строки КОДА, а не строки файла: пустые и комментарии не в счёт. Иначе правило
/// начало бы требовать удалять доккомментарии — ровно то, чем этот репозиторий и ценен.</para>
///
/// <para>Выход из правила есть и он осознанный: поднять число в
/// <c>src/file-size-baseline.json</c> тем же PR. Правка видна в диффе и требует фразы в описании.
/// Так живут <c>ComplexFields.tsx</c> (рекурсия схемы — дерево, разрывать нечем) и
/// <c>BackupService.Restore.cs</c>.</para>
/// </summary>
public class FileSizeRatchetTests
{
    /// <summary>Порог, с которого файл попадает под присмотр. Ниже него никто ничего не считает.</summary>
    private const int Threshold = 500;

    /// <summary>
    /// Свойством, а не полем: инициализаторы статических полей выполняются в порядке ТЕКСТА, и поле
    /// получило бы <c>RepoRoot</c> ещё пустым (объявлен ниже) — падало с «Value cannot be null».
    /// </summary>
    private static string BaselinePath => Path.Combine(RepoRoot, "src", "file-size-baseline.json");

    /// <summary>
    /// Что смотрим — ВЕСЬ рукописный код репозитория, а не только два дерева `src`.
    ///
    /// <para>Правило обещает «новый файл выше порога не заводится вовсе», и обещание обязано
    /// совпадать с охватом: вне присмотра оставались <c>deploy/update.sh</c> и
    /// <c>deploy/install.sh</c> — самые большие рукописные файлы репозитория и ровно те склады,
    /// против которых правило заведено (поймано ревью PR #1042).</para>
    /// </summary>
    private static readonly string[] ScannedDirs = ["src", "deploy", "docs/tools"];

    private static readonly string[] ScannedExt = [".cs", ".ts", ".tsx", ".js", ".mjs", ".sh"];

    /// <summary>
    /// Генерируемое и чужое. <c>Migrations</c> — вывод EF: файлы по 2300 строк, которые никто не
    /// пишет руками и никто не читает.
    /// </summary>
    private static readonly string[] SkipSegments =
        ["/obj/", "/bin/", "/Migrations/", "/node_modules/", "/dist/", "/coverage/"];

    [Fact]
    public void Файлы_не_растут_сверх_базового_уровня()
    {
        var baseline = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(BaselinePath))
            ?? throw new InvalidOperationException($"Не читается базовый уровень {BaselinePath}");
        var actual = Measure();
        var problems = new List<string>();

        foreach (var (path, measured) in actual.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var lines = measured.Lines;

            // Незакрытый блочный комментарий до конца файла. Обычно это `/*` внутри строкового
            // литерала (репозиторий встраивает и Typst, и JS): счётчик с него и до конца файла
            // считает код комментарием, то есть слепнет — а файл при этом можно наращивать
            // сколько угодно, и проверка будет молчать. Лучше громкий отказ, чем тихая слепота.
            if (measured.UnterminatedBlock)
            {
                problems.Add(
                    $"СЧЁТЧИК ОСЛЕП: {path} — файл кончился внутри блочного комментария. Если это `/*` " +
                    "внутри строки, вынесите литерал или закройте комментарий: иначе размер этого файла " +
                    "не проверяется вовсе.");
                continue;
            }

            var known = baseline.TryGetValue(path, out var allowed);
            if (!known)
            {
                if (lines > Threshold)
                    problems.Add(
                        $"НОВЫЙ файл выше порога: {path} — {lines} строк кода при пороге {Threshold}. " +
                        "Разделите его по занятиям; если файл обязан быть таким (общая рекурсия, один " +
                        $"неразрывный алгоритм) — впишите в базовый уровень и объясните в описании PR: \"{path}\": {lines}");
            }
            else if (lines > allowed)
                problems.Add(
                    $"ВЫРОС: {path} — было {allowed}, стало {lines}. Вынесите добавленное в соседний файл; " +
                    $"если рост осознан — поднимите уровень тем же PR: \"{path}\": {lines}");
            else if (lines <= Threshold)
                problems.Add(
                    $"ОПУСТИЛСЯ НИЖЕ ПОРОГА: {path} — было {allowed}, стало {lines}, это меньше порога " +
                    $"{Threshold}. Уберите запись из базового уровня совсем: файл больше не на присмотре, " +
                    "и оставленная запись разрешила бы ему отрасти обратно молча.");
            else if (lines < allowed)
                problems.Add(
                    $"ПОХУДЕЛ, а уровень прежний: {path} — было {allowed}, стало {lines}. Опустите: \"{path}\": {lines}. " +
                    "Без этого храповик прокручивается назад: разрезали один файл, раздули другой — и проверка молчит.");
        }

        foreach (var path in baseline.Keys.Where(p => !actual.ContainsKey(p)).OrderBy(p => p, StringComparer.Ordinal))
            problems.Add($"В базовом уровне есть запись о файле, которого нет: {path}. Уберите её — иначе список " +
                         "перестанет соответствовать репозиторию, а значит перестанет читаться.");

        Assert.True(problems.Count == 0,
            $"Храповик размера ({BaselinePath}):\n  " + string.Join("\n  ", problems));
    }

    private readonly record struct FileSize(int Lines, bool UnterminatedBlock);

    /// <summary>
    /// Путь → строки кода, по ВСЕМ найденным файлам. Порог применяется выше, при разборе: файл из
    /// базового уровня, похудевший ниже порога, обязан дойти сюда — иначе он выглядел бы удалённым,
    /// и успешный путь («разрезал склад») отвечал бы сообщением про несуществующий файл.
    /// </summary>
    private static Dictionary<string, FileSize> Measure()
    {
        var result = new Dictionary<string, FileSize>(StringComparer.Ordinal);
        foreach (var dir in ScannedDirs)
        {
            var full = Path.Combine(RepoRoot, dir.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(full)) continue;
            foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            {
                if (!ScannedExt.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                var rel = Path.GetRelativePath(RepoRoot, file).Replace('\\', '/');
                if (SkipSegments.Any(s => ("/" + rel).Contains(s, StringComparison.Ordinal))) continue;
                result[rel] = CodeLines(file);
            }
        }
        return result;
    }

    /// <summary>
    /// Строки кода: без пустых и без комментариев. Синтаксис комментариев у C# и TS/TSX общий, у
    /// shell — свой (<c>#</c>, включая shebang), поэтому счётчик один, а правило строки зависит от
    /// расширения.
    ///
    /// <para>⚠️ Это разбор ПО СТРОКАМ, а не лексер: строковые литералы он не понимает. Строка
    /// литерала, начинающаяся с <c>/*</c>, включит режим комментария. Случай «и не закрылась до
    /// конца файла» ловится флагом <see cref="FileSize.UnterminatedBlock" /> и громким отказом;
    /// случай «дальше по файлу встретилось <c>*/</c>» даст занижение счёта молча. Лексер тут был бы
    /// не по размеру задачи — предел назван, чтобы о нём знали, а не чтобы о нём догадывались.</para>
    /// </summary>
    private static FileSize CodeLines(string path)
    {
        var shell = Path.GetExtension(path).Equals(".sh", StringComparison.OrdinalIgnoreCase);
        var count = 0;
        var inBlock = false;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (inBlock)
            {
                if (line.Contains("*/", StringComparison.Ordinal)) inBlock = false;
                continue;
            }
            if (line.Length == 0) continue;
            if (shell)
            {
                if (line.StartsWith('#')) continue;
                count++;
                continue;
            }
            if (line.StartsWith("/*", StringComparison.Ordinal))
            {
                if (!line.Contains("*/", StringComparison.Ordinal)) inBlock = true;
                continue;
            }
            // `*` — продолжение блочного комментария; `{/*` … `*/}` — комментарий в разметке JSX.
            if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('*')) continue;
            if (line.StartsWith("{/*", StringComparison.Ordinal) && line.EndsWith("*/}", StringComparison.Ordinal)) continue;
            count++;
        }
        return new FileSize(count, inBlock);
    }

    /// <summary>Корень репозитория: решение лежит в <c>src/server</c>, остальные деревья — выше.</summary>
    private static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Не найден корень репозитория (CLAUDE.md) выше " + AppContext.BaseDirectory +
                " — сторож меряет исходники всего репозитория, и без корня мерить нечего.");
    }
}
