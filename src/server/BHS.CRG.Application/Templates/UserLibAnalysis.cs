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

    /// <summary>Имя файла-зонда, который проверка кладёт рядом с деревом.</summary>
    public const string ProbeName = "check.typ";

    /// <summary>
    /// Диагностика самого зонда — её пользователю не показать: он его не писал и не увидит
    /// (issue #509). Проверяем ТОЛЬКО имя и только среди путей, которые <see cref="ToLibPath"/> не
    /// смог привести к дереву: «userlib/check.typ» — законное имя файла дерева, но оно приводится, и
    /// сюда не попадает. Сравнение с путём временной папки не годилось: Typst печатает путь
    /// канонизированным, а временная папка канонизируется иначе (короткие имена 8.3 в Windows,
    /// «/private/var/…» в macOS) — фильтр молча переставал совпадать, и ошибка зонда приезжала бы
    /// админу сырым путём, вдобавок красной полосой «генерация не пройдёт».
    /// </summary>
    public static bool IsProbePath(string diagnosticPath)
    {
        var file = diagnosticPath.Replace('\\', '/').TrimEnd('/');
        var cut = file.LastIndexOf('/');
        return (cut < 0 ? file : file[(cut + 1)..]) == ProbeName;
    }

    // #import "путь" / #include "путь" — путь в двойных кавычках. Пакетные координаты
    // («@ns/name:1.0.0») сюда не подходят и не должны: дерево локальное.
    //
    // #include учитываем наравне с #import (issue #506): это тоже ссылка на файл, и пока импорты вёл
    // не пользователь, разница была неважна. Теперь диалог удаления — единственная защита, и на
    // файле, на который ссылались только через #include, он говорил бы «ссылок нет», после чего
    // библиотека переставала бы собираться.
    private static readonly Regex ImportOrIncludeRe =
        new(@"#(?:import|include)\s+""([^""]+)""", RegexOptions.Compiled);

    // Только «#import ...: *» — для проверки одноимённых объявлений (issues #507, #508). Перекрыть
    // имя может лишь то, что попало в общую область целиком. Проверено на Typst 0.15.1:
    //
    //   #include "frag.typ"              — файл подключён, но имён не приносит («unknown variable»);
    //   #import "frag.typ" as t          — приносит только «t», свой #let shout ничего не перекрывает;
    //   #import "frag.typ": pad          — приносит только «pad», «shout» остаётся неизвестным.
    //
    // Считая эти формы наравне с «: *», мы обещали бы «Typst молча возьмёт объявление из файла,
    // импортированного последним» там, где ничего не перекрывается. Осознанный пробел: перекрытие
    // ИМЕНЕМ ИЗ СПИСКА («: pad» против своего «#let pad») мы не ловим — для этого нужен разбор
    // списка имён, а не путей; лучше промолчать, чем сказать неправду.
    private static readonly Regex WildcardImportRe =
        new(@"#import\s+""([^""]+)""\s*:\s*\*", RegexOptions.Compiled);

    /// <summary>
    /// Текст без комментариев. Снимаем их ДО разбора импортов (issue #498): импорты теперь ведёт
    /// пользователь (#492), и временно закомментированная строка не должна считаться живой ссылкой —
    /// иначе файл числится подключённым и попадает в проверку одноимённых объявлений.
    ///
    /// Проходом, а не регулярным выражением (issue #504): блочные комментарии Typst ВЛОЖЕННЫЕ, а
    /// нежадное <c>/\*[\s\S]*?\*/</c> закрывает блок на первом же внутреннем <c>*/</c> — в
    /// <c>/* /* */ #import … */</c> импорт оставался бы живым. Закомментировать область, внутри
    /// которой уже есть комментарий, — обычное действие редактора.
    ///
    /// Строковые литералы СОХРАНЯЮТСЯ (issue #501): в них лежат пути импортов, а «/*» внутри строки
    /// иначе открывал бы мнимый блок, «//» в ссылке («https://…») съедал бы остаток строки. Перенос
    /// внутри литерала не допускается намеренно: непарную кавычку в разметке считаем обычным
    /// символом и идём дальше, вместо того чтобы проглотить полфайла до следующей кавычки.
    ///
    /// Переводы строк из комментариев сохраняются: <c>#let</c> считается только в начале строки.
    /// Зеркало клиентского <c>userLibTree.stripComments</c> — расхождение в понимании одного файла
    /// недопустимо.
    /// </summary>
    private static string StripComments(string content)
    {
        var sb = new System.Text.StringBuilder(content.Length);
        var depth = 0;
        var i = 0;

        while (i < content.Length)
        {
            if (depth > 0)
            {
                if (Starts(content, i, "/*")) { depth++; i += 2; }
                else if (Starts(content, i, "*/")) { depth--; i += 2; }
                else { if (content[i] == '\n') sb.Append('\n'); i++; }
                continue;
            }
            if (Starts(content, i, "/*")) { depth = 1; i += 2; continue; }
            if (Starts(content, i, "//"))
            {
                while (i < content.Length && content[i] != '\n') i++;
                continue;
            }
            if (content[i] == '`')
            {
                var end = RawSpanEnd(content, i);
                if (end > i)
                {
                    // Содержимое СНИМАЕМ, как комментарий (строки, наоборот, сохраняем — в них пути
                    // импортов). Внутри сырого блока не код: «#import» там показан, а не выполнен, и
                    // считать его ссылкой значило бы обещать «на файл ссылаются» там, где её нет.
                    for (var k = i; k < end; k++) if (content[k] == '\n') sb.Append('\n');
                    i = end;
                    continue;
                }
                // Прогон без пары переносим ЦЕЛИКОМ (issue #509). Вернувшись в сканер на второй
                // кавычке, мы открыли бы мнимый одинарный блок до следующей кавычки в файле.
                while (i < content.Length && content[i] == '`') { sb.Append('`'); i++; }
                continue;
            }
            if (content[i] == '"')
            {
                var j = i + 1;
                while (j < content.Length && content[j] != '"' && content[j] != '\n')
                    j += content[j] == '\\' ? 2 : 1;
                if (j < content.Length && content[j] == '"')
                {
                    sb.Append(content, i, j - i + 1);
                    i = j + 1;
                    continue;
                }
            }
            sb.Append(content[i]);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Конец сырого блока Typst, начатого в позиции <paramref name="i"/> (issue #506): один и тот же
    /// прогон обратных кавычек открывает и закрывает его. Внутри — не код: библиотека вправе
    /// показывать там синтаксис с непарным «/*», и без этой ветки такой блок открывал бы мнимый
    /// комментарий и съедал остаток файла — тот же класс, что «/*» в строке (#501) и вложенные
    /// комментарии (#504), этажом ниже.
    ///
    /// Ровно две кавычки — ПУСТОЙ сырой литерал, законченный сам по себе (проверено на Typst 0.15.1:
    /// файл с ним компилируется). Ища ему пару, мы находили её в следующем сыром блоке файла и
    /// съедали всё между ними — вместе с импортами (issue #509). Прогон без пары возвращает
    /// <paramref name="i"/>: вызывающий переносит его целиком как текст.
    /// </summary>
    private static int RawSpanEnd(string content, int i)
    {
        var n = 0;
        while (i + n < content.Length && content[i + n] == '`') n++;
        if (n == 2) return i + 2;
        var fence = new string('`', n);
        var close = content.IndexOf(fence, i + n, StringComparison.Ordinal);
        return close < 0 ? i : close + n;
    }

    private static bool Starts(string s, int i, string token) =>
        i + token.Length <= s.Length && string.CompareOrdinal(s, i, token, 0, token.Length) == 0;

    // Объявления верхнего уровня: строка начинается с #let/#show-независимого let. Вложенные let
    // (внутри тела функции) наружу не экспортируются, поэтому берём только неотступленные.
    private static readonly Regex TopLevelLetRe =
        new(@"(?m)^#let\s+([\p{L}\p{N}_][\p{L}\p{N}_-]*)", RegexOptions.Compiled);

    /// <summary>
    /// Пути, ПОДКЛЮЧЁННЫЕ к точке входа — по <c>#import</c> и по <c>#include</c>. Это «входит в
    /// сборку»: такой файл компилятор разбирает, и синтаксическая ошибка в нём останавливает
    /// генерацию всех документов. Пути в результате — относительные от <c>userlib/</c>.
    /// </summary>
    public static HashSet<string> ReachableFrom(string entrypointContent, IReadOnlyList<UserLibFile> files) =>
        Walk(entrypointContent, files, ImportOrIncludeRe);

    /// <summary>
    /// Пути, достижимые по <c>#import …: *</c> (issues #507, #508) — то есть те, чьи имена целиком
    /// попадают в общую область и могут перекрыть друг друга. Ни <c>#include</c>, ни импорт с
    /// псевдонимом, ни выборочный импорт этого не делают — проверено на Typst 0.15.1.
    /// </summary>
    public static HashSet<string> ImportedFrom(string entrypointContent, IReadOnlyList<UserLibFile> files) =>
        Walk(entrypointContent, files, WildcardImportRe);

    private static HashSet<string> Walk(
        string entrypointContent, IReadOnlyList<UserLibFile> files, Regex referenceRe)
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
            foreach (Match m in referenceRe.Matches(StripComments(content)))
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
                    // Ведущий «/» Typst считает от КОРНЯ проекта, а не от папки файла: компилятор
                    // зовётся с --root на временную папку, где дерево лежит в подпапке userlib/.
                    // Проверено на Typst 0.15.1 — такой импорт из файла дерева компилируется.
                    // Разрешая его как относительный, мы приставляли папку файла
                    // («gost/userlib/util/text.typ») и ссылку молча не находили: файл числился
                    // неподключённым, его одноимённые объявления выпадали из проверки, а удаление
                    // проходило без предупреждения (issue #505). У точки входа база — корень, там
                    // ведущий «/» и так безвреден.
                    target = raw.StartsWith('/') ? AbsoluteToTreePath(raw) : ResolveRelative(baseDir, raw);
                }

                if (target is null || !byPath.TryGetValue(target, out var file)) continue;
                if (!reachable.Add(target)) continue;   // уже обошли — заодно и защита от цикла

                var dir = target.Contains('/') ? target[..target.LastIndexOf('/')] : string.Empty;
                queue.Enqueue((dir, file.Content));
            }
        }

        return reachable;
    }

    /// <summary>
    /// Путь из диагностики Typst — в путь, которым оперирует интерфейс: точка входа как есть, файлы
    /// дерева без префикса <c>userlib/</c> (префикс постоянный и ничего не сообщает).
    /// <c>null</c> — путь не наш: так выглядит диагностика из файла пакета <c>@preview</c> или из
    /// служебного файла генерации.
    /// </summary>
    public static string? ToLibPath(string diagnosticPath)
    {
        var file = diagnosticPath.Replace('\\', '/');
        var prefix = UserLibPath.FolderName + "/";
        var idx = file.IndexOf(prefix, StringComparison.Ordinal);
        if (idx >= 0) return file[(idx + prefix.Length)..];
        return file.EndsWith(EntrypointName, StringComparison.Ordinal) ? EntrypointName : null;
    }

    /// <summary>
    /// Путь от корня проекта («/userlib/util/text.typ») — в координаты дерева («util/text.typ»).
    /// Всё, что вне <c>userlib/</c>, нашим файлом не является.
    /// </summary>
    private static string? AbsoluteToTreePath(string raw)
    {
        var fromRoot = ResolveRelative(string.Empty, raw);
        var prefix = UserLibPath.FolderName + "/";
        return fromRoot is not null && fromRoot.StartsWith(prefix, StringComparison.Ordinal)
            ? fromRoot[prefix.Length..]
            : null;
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

    /// <summary>
    /// Имена, объявленные в файле на верхнем уровне (то, что уходит наружу при <c>: *</c>).
    ///
    /// Комментарии снимаем, как и при разборе импортов (issue #500): закомментированное объявление
    /// не объявляет ничего. Иначе перенос функции в другой файл с закомментированным оригиналом —
    /// обычный приём — давал бы ложное «объявлено ещё в» на имя, объявленное ровно один раз.
    /// </summary>
    public static IReadOnlyList<string> TopLevelNames(string content) =>
        TopLevelLetRe.Matches(StripComments(content))
            .Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();

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
        // Именно ИМПОРТИРОВАННЫЕ, не просто подключённые (issue #507): перекрыть имя может только то,
        // что попало в общую область через `: *`, а #include имён не приносит.
        var reachable = ImportedFrom(entrypointContent, files);

        // Дубликаты ищем среди подключённых: одинаковые имена перекрывают друг друга ровно тогда,
        // когда оба файла попали в одну область через `: *` из точки входа.
        //
        // Сама точка входа — ПЕРВАЯ в этом списке (issue #506). Её собственные объявления живут в той
        // же области, что и вытащенные из дерева, и шаблон получает их вместе. Без неё дыра
        // приходилась ровно на тот приём, ради которого дерево и заводилось: вынес функцию из
        // userlib.typ в util/text.typ, дописал импорт, а оригинальный `#let` убрать забыл — Typst
        // молча берёт последнее связывание, шаблоны получают старую копию, и никто не предупредил.
        // Заметьте, закомментированный оригинал мы уже не считали объявлением (#500), то есть
        // молчали как раз про более вероятную ошибку — незакомментированный.
        var scanned = new[] { new UserLibFile(EntrypointName, entrypointContent) }
            .Concat(files.Where(f => reachable.Contains(f.Path)).OrderBy(f => f.Path, StringComparer.Ordinal));

        var declarations = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in scanned)
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
