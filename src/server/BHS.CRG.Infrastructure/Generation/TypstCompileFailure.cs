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
        ["systemlib.typ"] = "системная библиотека",
        [TypstGenerator.DataFileName] = "данные документа",
    };

    // Остаток абсолютного пути после вычистки папки прогона: диск Windows (в т.ч. с префиксом
    // «\\?\») или системный каталог Unix. Такой путь приходит не из нашей папки — из окружения
    // сервера, — и пользователю не адресует ничего.
    [GeneratedRegex(@"(\\\\\?\\)?[A-Za-z]:[\\/][^\s)""']*|/(?:tmp|home|root|usr|var|etc|opt|app)/[^\s)""']*")]
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
    /// <para>Сравнение по СУФФИКСУ, а не по равенству: Typst печатает абсолютный путь, на Windows
    /// ещё и с префиксом «\\?\», — тем же приёмом привязывает диагностики
    /// <see cref="TypstSyntaxChecker" />. Отбор по одному имени файла не совпал бы ни с чем, и
    /// сторож молча показывал бы ошибки без адреса.</para>
    /// </summary>
    private static string? Relative(string file, string runDir)
    {
        var path = Normalize(file);
        var root = Normalize(runDir).TrimEnd('/') + "/";
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path[root.Length..] : null;
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
    /// <para>⚠️ Формы перебираются от ДЛИННОЙ к короткой, и это не косметика. Сначала сняв короткую
    /// (<c>C:\…\tmp\&lt;guid&gt;\</c>), мы оставили бы от длинной висящий префикс <c>\\?\</c> — он уже
    /// ни к чему не приклеен, и следующая подстановка его не узнаёт. Поймано тестом на живом выводе
    /// «file not found», где путь лежит внутри сообщения.</para>
    /// </summary>
    private static string Scrub(string message, string runDir)
    {
        var roots = new[] { runDir, runDir.Replace('\\', '/') }.Where(r => !string.IsNullOrEmpty(r));
        var forms = roots
            .SelectMany(r => new[] { @"\\?\" + r, "//?/" + r, r })
            .SelectMany(f => new[] { f + '\\', f + '/', f })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(f => f.Length);

        var withoutRun = forms.Aggregate(message,
            (text, form) => text.Replace(form, string.Empty, StringComparison.OrdinalIgnoreCase));

        return AbsolutePathRe.Replace(withoutRun, "‹путь на сервере›").Trim();
    }
}
