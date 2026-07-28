using System.Text.RegularExpressions;

namespace BHS.CRG.Application.Templates;

/// <summary>Файл дерева библиотеки в виде, пригодном для анализа и материализации.</summary>
public record UserLibFile(string Path, string Content);

/// <summary>Замечание к библиотеке, не мешающее ей собраться, но почти наверняка означающее ошибку.</summary>
/// <param name="Path">Файл, к которому относится (пустой — общее).</param>
public record UserLibWarning(string Path, string Message);

/// <summary>
/// Разбор дерева библиотеки БЕЗ запуска Typst (issue #473).
///
/// Ловит режим отказа, который создало само разрезание одного файла на много и который компилятор
/// не считает ошибкой: <b>одноимённые объявления в разных файлах</b>. Проверено на Typst 0.15.1:
/// при двух <c>#import ...: *</c> с одинаковым именем побеждает последний импорт, БЕЗ
/// предупреждения. В одном файле два одинаковых <c>#let</c> видно глазом, в двадцати — нет.
/// </summary>
public static class UserLibAnalysis
{
    /// <summary>Имя точки входа — то же, что видит шаблон.</summary>
    public const string EntrypointName = "userlib.typ";

    // #import "путь": ... — путь в двойных кавычках. Пакетные координаты («@ns/name:1.0.0») сюда не
    // подходят и не должны: дерево локальное.
    private static readonly Regex ImportRe =
        new(@"#import\s+""([^""]+)""", RegexOptions.Compiled);

    // Комментарии Typst — строчные и блочные. Снимаем их ДО разбора импортов (issue #498): импорты
    // теперь ведёт пользователь (#492), и временно закомментированная строка не должна считаться
    // живой ссылкой — иначе файл числится подключённым и попадает в проверку одноимённых объявлений.
    private static readonly Regex CommentRe =
        new(@"/\*[\s\S]*?\*/|//[^\n]*", RegexOptions.Compiled);

    // Объявления верхнего уровня: строка начинается с #let/#show-независимого let. Вложенные let
    // (внутри тела функции) наружу не экспортируются, поэтому берём только неотступленные.
    private static readonly Regex TopLevelLetRe =
        new(@"(?m)^#let\s+([\p{L}\p{N}_][\p{L}\p{N}_-]*)", RegexOptions.Compiled);

    /// <summary>
    /// Пути, достижимые из точки входа по цепочке импортов. Пути в результате — относительные от
    /// <c>userlib/</c>, как и у самих файлов.
    /// </summary>
    public static HashSet<string> ReachableFrom(string entrypointContent, IReadOnlyList<UserLibFile> files)
    {
        var byPath = files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var reachable = new HashSet<string>(StringComparer.Ordinal);

        // Точка входа лежит в КОРНЕ временной папки, а дерево — в подпапке userlib/, поэтому её
        // импорты пишутся как «userlib/gost/f3.typ». У файлов дерева база — их собственная папка,
        // и для файла в корне дерева она ПУСТАЯ — отличать её от точки входа по пустой строке
        // нельзя: так «a.typ», импортирующий соседний «b.typ», переставал резолвиться.
        var queue = new Queue<(string? BaseDir, string Content)>();
        queue.Enqueue((null, entrypointContent));
        var entryImportsPrefix = UserLibPath.FolderName + "/";

        while (queue.Count > 0)
        {
            var (baseDir, content) = queue.Dequeue();
            foreach (Match m in ImportRe.Matches(CommentRe.Replace(content, string.Empty)))
            {
                var raw = m.Groups[1].Value.Replace('\\', '/');
                if (raw.StartsWith('@')) continue;   // координата пакета — не наш файл

                string? target;
                if (baseDir is null)
                {
                    // Из точки входа интересуют только ссылки внутрь дерева. Сначала НОРМАЛИЗУЕМ:
                    // пока строку писало приложение, она всегда была канонической, но теперь импорты
                    // ведёт пользователь (#492), и «./userlib/f3.typ» — совершенно обычная запись.
                    // Без нормализации подключённый файл объявлялся бы неподключённым, а попытка
                    // «починить» это вторым импортом дала бы предупреждение о дубликате имён.
                    var normalized = ResolveRelative(string.Empty, raw);
                    if (normalized is null || !normalized.StartsWith(entryImportsPrefix, StringComparison.Ordinal))
                        continue;
                    target = normalized[entryImportsPrefix.Length..];
                }
                else
                {
                    target = ResolveRelative(baseDir, raw);
                }

                if (target is null || !byPath.TryGetValue(target, out var file)) continue;
                if (!reachable.Add(target)) continue;   // уже обошли — заодно и защита от цикла

                var dir = target.Contains('/') ? target[..target.LastIndexOf('/')] : string.Empty;
                queue.Enqueue((dir, file.Content));
            }
        }

        return reachable;
    }

    /// <summary>Разрешение относительного пути «../../util/text.typ» от папки файла.</summary>
    private static string? ResolveRelative(string baseDir, string raw)
    {
        var parts = new List<string>(baseDir.Length == 0 ? [] : baseDir.Split('/'));
        foreach (var segment in raw.Split('/'))
        {
            switch (segment)
            {
                case "" or ".":
                    break;
                case "..":
                    if (parts.Count == 0) return null;   // ушли выше дерева — такого файла у нас нет
                    parts.RemoveAt(parts.Count - 1);
                    break;
                default:
                    parts.Add(segment);
                    break;
            }
        }
        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    /// <summary>Имена, объявленные в файле на верхнем уровне (то, что уходит наружу при <c>: *</c>).</summary>
    public static IReadOnlyList<string> TopLevelNames(string content) =>
        TopLevelLetRe.Matches(content).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Замечания по дереву — одноимённые объявления. Порядок устойчив: список показывается
    /// пользователю и не должен «прыгать» между сохранениями.
    ///
    /// Про НЕПОДКЛЮЧЁННЫЕ файлы больше не предупреждаем (issue #494). Пока импорты дописывало
    /// приложение, «не подключён» означало сбой; после передачи импортов пользователю (#492) это
    /// обычное промежуточное состояние — файл создан, функции пишутся, подключат позже. Вдобавок
    /// прежняя формулировка утверждала неправду: «его функции в шаблонах не появятся» — шаблон
    /// вправе импортировать файл дерева напрямую, минуя точку входа, и один из шаблонов так и
    /// делает. Правило теперь: сообщаем о том, что СЛОМАНО, и молчим о том, что НЕ ЗАКОНЧЕНО.
    /// </summary>
    public static IReadOnlyList<UserLibWarning> Warnings(
        string entrypointContent, IReadOnlyList<UserLibFile> files)
    {
        var warnings = new List<UserLibWarning>();
        var reachable = ReachableFrom(entrypointContent, files);

        // Дубликаты ищем только среди подключённых: одинаковые имена перекрывают друг друга ровно
        // тогда, когда оба файла попали в одну область через `: *` из точки входа.
        var declarations = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in files.Where(f => reachable.Contains(f.Path)).OrderBy(f => f.Path, StringComparer.Ordinal))
            foreach (var name in TopLevelNames(file.Content))
                (declarations.TryGetValue(name, out var list) ? list : declarations[name] = []).Add(file.Path);

        foreach (var (name, paths) in declarations.Where(d => d.Value.Count > 1).OrderBy(d => d.Key, StringComparer.Ordinal))
            foreach (var path in paths)
                warnings.Add(new UserLibWarning(path,
                    $"«{name}» объявлено ещё в: {string.Join(", ", paths.Where(p => p != path))}. "
                    + "Typst молча возьмёт объявление из файла, импортированного последним."));

        return warnings;
    }
}
