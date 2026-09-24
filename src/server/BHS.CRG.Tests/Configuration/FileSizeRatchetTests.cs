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
/// Так живут <c>ComplexFields.tsx</c> (рекурсия схемы — дерево, разрывать нельзя) и
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

    /// <summary>Что смотрим: свой код обоих деревьев. Генерируемое исключено путями ниже.</summary>
    private static readonly (string Dir, string[] Ext)[] Scanned =
    [
        (Path.Combine("src", "server"), [".cs"]),
        (Path.Combine("src", "client", "src"), [".ts", ".tsx"]),
        (Path.Combine("src", "client", "e2e"), [".mjs"]),
    ];

    /// <summary>
    /// Генерируемое и чужое. <c>Migrations</c> — вывод EF: файлы по 2300 строк, которые никто не
    /// пишет руками и никто не читает.
    /// </summary>
    private static readonly string[] SkipSegments = ["/obj/", "/bin/", "/Migrations/", "/node_modules/", "/dist/"];

    [Fact]
    public void Файлы_не_растут_сверх_базового_уровня()
    {
        var baseline = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(BaselinePath))
            ?? throw new InvalidOperationException($"Не читается базовый уровень {BaselinePath}");
        var actual = Measure();
        var problems = new List<string>();

        foreach (var (path, lines) in actual.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var known = baseline.TryGetValue(path, out var allowed);
            if (!known && lines > Threshold)
                problems.Add(
                    $"НОВЫЙ файл выше порога: {path} — {lines} строк кода при пороге {Threshold}. " +
                    "Разделите его по занятиям; если файл обязан быть таким (общая рекурсия, один " +
                    $"неразрывный алгоритм) — впишите в базовый уровень и объясните в описании PR: \"{path}\": {lines}");
            else if (known && lines > allowed)
                problems.Add(
                    $"ВЫРОС: {path} — было {allowed}, стало {lines}. Вынесите добавленное в соседний файл; " +
                    $"если рост осознан — поднимите уровень тем же PR: \"{path}\": {lines}");
            else if (known && lines < allowed)
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

    /// <summary>Путь → строки кода. Ключи с прямым слешем и от корня репозитория: они лежат в JSON.</summary>
    private static Dictionary<string, int> Measure()
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (dir, exts) in Scanned)
        {
            var full = Path.Combine(RepoRoot, dir);
            if (!Directory.Exists(full)) continue;
            foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            {
                if (!exts.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                var rel = Path.GetRelativePath(RepoRoot, file).Replace('\\', '/');
                if (SkipSegments.Any(s => ("/" + rel).Contains(s, StringComparison.Ordinal))) continue;
                var lines = CodeLines(file);
                if (lines > Threshold) result[rel] = lines;
            }
        }
        return result;
    }

    /// <summary>
    /// Строки кода: без пустых и без комментариев. Синтаксис комментариев у C# и TS/TSX общий,
    /// поэтому счётчик один на оба дерева.
    /// </summary>
    private static int CodeLines(string path)
    {
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
        return count;
    }

    /// <summary>Корень репозитория: решение лежит в <c>src/server</c>, оба дерева — двумя уровнями выше.</summary>
    private static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Не найден корень репозитория (CLAUDE.md) выше " + AppContext.BaseDirectory +
                " — сторож меряет исходники обоих деревьев, и без корня мерить нечего.");
    }
}
