using System.Text.RegularExpressions;
using BHS.CRG.Application.Generation;

namespace BHS.CRG.Infrastructure.Generation;

/// <summary>
/// Отказ компилятора шаблона — в текст для человека (issue #1047).
///
/// <para>Зачем. Шаблон, который обращается к полю, отсутствующему в данных, — единственный отказ
/// генерации, причину которого пользователь устраняет САМ: поправить шаблон или заполнить поле.
/// Прежде такой отказ приходил как «Внутренняя ошибка сервера» с идентификатором запроса, то есть
/// человек видел поломку сервера и шёл к администратору, хотя сервер исправен и не сошлись две
/// вещи, которые правит он.</para>
///
/// <para>⚠️ <b>Вывод компилятора наружу не уходит.</b> Typst печатает АБСОЛЮТНЫЕ пути временной
/// папки — и в адресе диагностики, и внутри самого текста: <c>file not found (searched at
/// \\?\C:\…\Temp\&lt;guid&gt;\assets\missing.png)</c>. Снято с живого компилятора, не предположено.
/// Поэтому путь адреса заменяется относительным (или не называется вовсе), а текст сообщения
/// вычищается от папки прогона, после чего проверяется ещё раз — на случай пути, пришедшего не из
/// неё (шрифты, кеш пакетов).</para>
///
/// <para>Разобрать удалось — отказ наш (400, текст дословно пользователю). Не удалось — исключение
/// остаётся чужим и уходит в 500 с идентификатором запроса: «компилятор сказал нечто, чего мы не
/// понимаем» — это не про шаблон, это про установку.</para>
/// </summary>
public static partial class TypstCompileFailure
{
    /// <summary>Сколько диагностик показывать. Первые ошибки ведут к остальным — простыня не помогает.</summary>
    private const int MaxShown = 5;

    /// <summary>Потолок на длину одной строки компилятора: наружу идёт сообщение, а не дамп.</summary>
    private const int MaxMessageLength = 300;

    /// <summary>
    /// Файлы прогона — в термины пользователя. Ключ — путь относительно папки прогона.
    ///
    /// <para><c>template.typ</c> называется «шаблон документа» и его номера строк совпадают с
    /// номерами в редакторе: шаблон пишется в папку прогона ДОСЛОВНО, без подстановки импортов
    /// (issue #353). Значит «строка 8» — это строка 8 в редакторе, по ней можно ткнуть.</para>
    /// </summary>
    private static readonly Dictionary<string, string> KnownFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["template.typ"] = "шаблон документа",
        [TypstGenerator.TypeBlocksFileName] = "блоки отображения типов",
        [TypstGenerator.UserLibFileName] = "библиотека Typst",
        [SystemTypstLib.FileName] = "системная библиотека",
        [TypstGenerator.DataFileName] = "данные документа",
    };

    // Остаток абсолютного пути после вычистки папки прогона: диск Windows (в т.ч. с префиксом
    // «\\?\») или системный каталог Unix. Такой путь приходит не из нашей папки — из окружения
    // сервера, — и пользователю не адресует ничего.
    [GeneratedRegex(@"(\\\\\?\\)?[A-Za-z]:[\\/][^\s)""']*|/(?:private|tmp|home|root|usr|var|etc|opt|app)/[^\s)""']*")]
    private static partial Regex AbsolutePathRe { get; }

    /// <summary>
    /// Отказ по выводу компилятора — или <c>null</c>, если разобрать нечего: тогда бросать нужно
    /// прежнее исключение, чтобы отказ ушёл в 500 с идентификатором запроса и попал в журнал.
    /// </summary>
    /// <param name="stdErr">Вывод компилятора в формате <c>--diagnostic-format short</c>.</param>
    /// <param name="runDir">Папка прогона: её путь вычищается из адресов и сообщений.</param>
    public static TemplateCompilationException? TryDescribe(string? stdErr, string runDir)
    {
        var errors = TypstShortDiagnostics.Parse(stdErr).Where(d => d.Severity == "error").ToList();
        if (errors.Count == 0) return null;

        var lines = errors.Take(MaxShown)
            .Select(d => "  " + Where(d.File, d.Line, runDir) + Text(d.Message, runDir))
            .ToList();
        if (errors.Count > MaxShown)
            lines.Add($"  …и ещё {errors.Count - MaxShown} — первые ошибки обычно ведут к остальным.");

        return new TemplateCompilationException(
            "Шаблон не собрался, поэтому документ не сформирован. Это не поломка сервера: не сошлись "
            + "шаблон и данные документа — поправьте шаблон или заполните поле, к которому он обращается.\n"
            + string.Join("\n", lines));
    }

    /// <summary>Адрес ошибки: «шаблон документа, строка 8: ». Без пути, если он не из нашей папки.</summary>
    private static string Where(string file, int line, string runDir)
    {
        var relative = Relative(file, runDir);
        if (relative is null) return $"строка {line}: ";
        var name = KnownFiles.TryGetValue(relative, out var human) ? human : relative;
        return $"{name}, строка {line}: ";
    }

    /// <summary>
    /// Путь диагностики — относительно папки прогона, или <c>null</c>, если он вне её.
    ///
    /// <para>⚠️ Отрезается всё до ИМЕНИ папки прогона, а не сверяется её полный путь. Сверка полного
    /// пути в этом решении уже подводила: она зависит от того, как хост канонизирует временную папку
    /// (короткие имена 8.3, «/private/var/…» вместо «/var/…»), и МОЛЧА перестаёт совпадать — см.
    /// <see cref="UserLibChecker" />, где этот приём именно поэтому и убрали. Последствие тихое и
    /// поэтому скверное: адрес ошибки исчезает, а в тексте сообщения путь не вычищается и целиком
    /// уходит под заглушку — вместе с «assets/missing.png», то есть с единственным, что человеку и
    /// нужно было. Имя папки задаём мы сами (GUID), канонизация его не трогает.</para>
    /// </summary>
    private static string? Relative(string file, string runDir)
    {
        var path = Normalize(file);

        var leaf = LeafOf(runDir);
        if (leaf.Length > 0)
        {
            var marker = "/" + leaf + "/";
            var at = path.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at >= 0) return path[(at + marker.Length)..];
        }

        // Путь напечатан относительным — значит он уже относителен папке прогона (рабочая папка
        // процесса — она). Абсолютный путь, не принадлежащий прогону, адресом быть не может.
        return !path.StartsWith('/') && !path.Contains(':') ? path : null;
    }

    /// <summary>Имя папки прогона — то, по чему её узнают в любом написании пути.</summary>
    private static string LeafOf(string runDir)
    {
        var s = Normalize(runDir).TrimEnd('/');
        var at = s.LastIndexOf('/');
        return at < 0 ? s : s[(at + 1)..];
    }

    private static string Normalize(string path)
    {
        var s = path.Replace('\\', '/').Trim();
        return s.StartsWith("//?/", StringComparison.Ordinal) ? s[4..] : s;
    }

    /// <summary>
    /// Текст компилятора — вычищенный от путей и урезанный. Английский оставлен как есть: перевести
    /// произвольное сообщение Typst нечем, а выдумать перевод — значит соврать о причине.
    ///
    /// <para>Переведён ровно один случай — отсутствующий ключ словаря. Он назван в issue #1047 как
    /// повод и встречается чаще остальных: шаблон обращается к полю, которого в данных нет. Про
    /// СЛОВАРЬ, а не про «поле документа»: словарём в шаблоне может быть что угодно, и назвать
    /// чужую структуру данными документа было бы догадкой.</para>
    /// </summary>
    private static string Text(string message, string runDir)
    {
        var cleaned = Scrub(message, runDir);
        if (cleaned.Length > MaxMessageLength) cleaned = cleaned[..MaxMessageLength] + "…";

        var key = DictionaryKeyRe.Match(cleaned);
        return key.Success ? $"в словаре нет ключа «{key.Groups["key"].Value}»" : cleaned;
    }

    [GeneratedRegex(@"^dictionary does not contain key ""(?<key>[^""]*)""$")]
    private static partial Regex DictionaryKeyRe { get; }

    /// <summary>
    /// Путь папки прогона — вон из текста, остатки абсолютных путей — под заглушку.
    ///
    /// <para>Правило то же, что у <see cref="Relative" />: снимается всё, что кончается именем папки
    /// прогона и разделителем, — с любым написанием ведущей части (<c>\\?\</c>, «/private/var/…»,
    /// короткие имена). Перебор написаний полного пути, стоявший здесь прежде, мало того что зависел
    /// от канонизации, так ещё и рвался на порядке: сняв короткую форму первой, он оставлял от
    /// длинной висящий префикс <c>\\?\</c>, которого следующая подстановка уже не узнавала (поймал
    /// тест на живом выводе «file not found», где путь лежит ВНУТРИ сообщения).</para>
    /// </summary>
    private static string Scrub(string message, string runDir)
    {
        var leaf = LeafOf(runDir);
        var withoutRun = leaf.Length == 0
            ? message
            : new Regex($@"[^\s""']*{Regex.Escape(leaf)}[\\/]",
                RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)).Replace(message, string.Empty);

        return AbsolutePathRe.Replace(withoutRun, "‹путь на сервере›").Trim();
    }
}
