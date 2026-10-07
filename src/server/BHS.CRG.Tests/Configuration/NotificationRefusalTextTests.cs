using System.Text.RegularExpressions;
using BHS.CRG.Tests.Common;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Человеку показывают текст, написанный для человека, — а не сообщение чужого исключения
/// (правило issue #691, двери найдены ревью PR #1049 и issue #1059).
///
/// <para>Зачем сторож. Уведомление и строка ошибки на экране — такие же выходы наружу, как ответ на
/// запрос, и правило у них обязано быть одно: дословно доходит только наш отказ, чужое заменяется
/// общим текстом, а подробности идут в журнал. У ответа это правило проверено
/// (<see cref="ApiErrorMappingTests" />) и держится конвейером — одним местом на все запросы. У
/// остальных выходов общего места нет: текст собирает каждый вызов сам, и <c>ex.Message</c> в нём
/// выглядит как забота о пользователе.</para>
///
/// <para>Найдено на живом примере дважды. Сначала в колокольчике: два вызова из двадцати двух
/// передавали сообщение чужого исключения дословно — генерация документа и распознавание; через
/// первый уезжал вывод компилятора шаблона с путями папки прогона, через второй — ответ стороннего
/// сервиса вместе с его адресом. Потом в предпросмотрах: поле ошибки DTO несло <c>ex.Message</c> из
/// <c>catch (Exception)</c> вокруг чтения источника, то есть текст Npgsql со строкой подключения и
/// ответ хранилища с именем бакета — прямо в таблицу на экране, а через <c>rowsError</c> — ещё и в
/// ответ MCP-инструмента, внешнему агенту. Двери закрывались ПООДИНОЧКЕ: следующий вызов напишут
/// так же, и компилятор не возразит. Поэтому проверка смотрит на форму вызова, а не на нынешний
/// состав.</para>
///
/// <para>Санкционированная форма — <c>Refusals.TextOr(ex, «общий текст»)</c>: она и отделяет наш
/// отказ от чужого, и заставляет автора придумать этот общий текст.</para>
///
/// <para>⚠️ Что сторожу НЕ видно: текст, собранный ни конструктором, ни присваиванием в
/// поле-«причину», а сложенный в обычный список строк (так устроен
/// <c>PrefixedAddressingReport.Blocked</c>). Зацепиться там не за что, и такие места держатся на
/// ревью — держите это в уме, заводя ещё один список «причин для человека».</para>
/// </summary>
public class NotificationRefusalTextTests
{
    private static readonly string[] Projects =
        SolutionModules.WithCore("BHS.CRG.Api", "BHS.CRG.Application", "BHS.CRG.Infrastructure");

    /// <summary>
    /// Осознанное исключение из правила: файл, ОБРЫВОК самого вызова и причина.
    ///
    /// <para>Обрывок обязателен: исключение, выписанное на весь файл, прикрыло бы и следующую
    /// утечку в нём — уже настоящую, с текстом базы или хранилища, — а сверка «исключение ещё
    /// нужно» считала бы его использованным. Обрывок привязывает разрешение к одному вызову.</para>
    /// </summary>
    private readonly record struct Exemption(string File, string Fragment, string Reason);

    /// <summary>
    /// Пустой список — не признак, что правило лишнее: он ровно то, к чему стремились. Добавляя
    /// строку, вы принимаете решение.
    /// </summary>
    private static readonly Exemption[] NotificationDeliberate = [];

    /// <inheritdoc cref="NotificationDeliberate" />
    private static readonly Exemption[] PreviewDeliberate =
    [
        new("ComputedFieldResolver.cs", "computed-error",
            "Ошибка расчётного поля: сообщение движка формул — единственный отклик автору " +
            "выражения, и говорит оно ровно о его выражении («x is not defined»). Чужому тексту " +
            "здесь взяться неоткуда: вычисление идёт над словарём уже собранных значений — без " +
            "базы, хранилища и сети, — так что споткнуться можно только о саму формулу."),
        new("ValidateTypstBlocks.cs", "\"syntax\"",
            "Синтаксические ошибки блоков: `e` здесь — не исключение, а разобранная диагностика " +
            "Typst (TypstSyntaxError), и показать её автору блока — смысл всей проверки. Текст уже " +
            "привязан к файлу и строке блока, а сбой самого инструмента приходит другой веткой и " +
            "заменяется общим текстом."),
    ];

    [Fact]
    public void A_notification_never_carries_a_foreign_exception_message()
        => AssertNoForeignMessage(
            ["PublishAsync("], [], NotificationDeliberate,
            "Уведомление несёт сообщение чужого исключения:",
            "Через колокольчик так уезжают вывод компилятора шаблона с путями папки прогона, текст\n" +
            "Npgsql со строкой подключения и ответы сторонних сервисов вместе с их адресами.");

    /// <summary>
    /// Те же проверки — местам, через которые текст ошибки попадает наружу не уведомлением
    /// (issue #1059): предпросмотр материализации источника, экран «Проверка связок», диагностика
    /// генерации, проверка Typst-блоков и <c>rowsError</c> в ответе MCP-инструмента. Выбраны не
    /// «все DTO подряд», а ровно те, у которых есть поле с текстом ошибки и которые собираются в
    /// <c>catch</c>: именно там соблазн отдать <c>ex.Message</c> максимален — иначе причина как
    /// будто исчезает совсем.
    /// </summary>
    [Fact]
    public void A_preview_shown_to_a_person_never_carries_a_foreign_exception_message()
        => AssertNoForeignMessage(
            ["MaterializePreviewDto(", "BindingPreviewDto(", "ResolutionDiagnostic(", "TypstBlockProblem("],
            ErrorAssignments, PreviewDeliberate,
            "Строка ошибки, показанная человеку, несёт сообщение чужого исключения:",
            "Эти места собираются в catch вокруг чтения источника и запуска внешних программ, то\n" +
            "есть в их поле ошибки уезжают текст Npgsql со строкой подключения, ответ хранилища с\n" +
            "именем бакета и путь к исполняемому файлу на сервере — и показываются прямо на экране\n" +
            "(а rowsError — ещё и в ответе MCP-инструмента, то есть внешнему агенту).");

    private static void AssertNoForeignMessage(
        string[] markers, Regex[] assignments, Exemption[] deliberate, string headline, string what)
    {
        var offenders = new List<string>();
        var used = new HashSet<int>();

        foreach (var project in Projects)
        {
            // Общий обход (issue #1071): без obj, bin и миграций — и с отказом на проект, которого нет,
            // вместо прежнего молчаливого пропуска.
            foreach (var file in SourceTree.Files(project))
            {
                var name = Path.GetFileName(file);
                var text = File.ReadAllText(file);
                var access = MessageAccessIn(text);

                foreach (var call in Fragments(text, markers, assignments))
                {
                    if (!access.IsMatch(call)) continue;

                    // Сработавшее исключение отмечаем ДО отсева: иначе закрытая дверь оставила бы
                    // в списке строку, которая уже ничего не разрешает, — и следующая такая же
                    // ошибка прошла бы молча, прикрывшись чужой причиной.
                    var at = Array.FindIndex(deliberate,
                        e => e.File == name && call.Contains(e.Fragment, StringComparison.Ordinal));
                    if (at >= 0) used.Add(at);
                    else offenders.Add($"{name}: {Shorten(call)}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            headline + "\n  " + string.Join("\n  ", offenders) + "\n\n" + what + "\n" +
            "Оберните текст в Refusals.TextOr(ex, «общий текст»): наш отказ дойдёт дословно, чужой —\n" +
            "заменится, а исключение целиком запишет журнал рядом. Если сообщение здесь заведомо\n" +
            "наше, впишите вызов в список осознанных исключений с причиной — это решение, а не\n" +
            "формальность.");

        var stale = deliberate.Where((_, i) => !used.Contains(i)).Select(e => $"{e.File} ({e.Fragment})").ToList();
        Assert.True(stale.Count == 0,
            "Исключение из правила больше ничего не разрешает: " + string.Join(", ", stale) + "\n" +
            "Дверь закрыли, а строку не убрали — и следующий ex.Message в этом месте пройдёт молча,\n" +
            "прикрывшись чужой причиной. Удалите строку из списка.");
    }

    /// <summary>
    /// Места, где собирается текст для человека: аргументы вызовов-маркеров и присваивания в
    /// поле-«причину».
    /// </summary>
    private static IEnumerable<string> Fragments(string text, string[] markers, Regex[] assignments)
    {
        foreach (var marker in markers)
            foreach (var call in CallsTo(text, marker))
                yield return call;

        foreach (var assignment in assignments)
            foreach (Match m in assignment.Matches(text))
                yield return m.Value;
    }

    /// <summary>
    /// Присваивание в поле-«причину»: <c>rowsError = …</c>, <c>state.LastError = …</c>.
    ///
    /// Конструктором такое место не поймать — текст кладётся в уже созданный объект, и именно так
    /// устроен <c>rowsError</c> в ответе MCP-инструмента <c>get_source</c> (дверь нашло ревью
    /// PR #1058). Адресат там даже не человек, а внешний агент, и сообщение Npgsql уезжало к нему
    /// целиком.
    /// </summary>
    private static readonly Regex[] ErrorAssignments =
    [
        new(@"\b[\w.]*(?:Error|Warning)\s*(?:\?\?)?=(?!=)[^;]{0,400}",
            RegexOptions.Compiled | RegexOptions.Singleline),
    ];

    /// <summary>
    /// Аргументы каждого вызова <paramref name="marker" /> — по балансу скобок, а не построчно:
    /// текст почти всегда перенесён на следующие строки, и построчная проверка не увидела бы ровно
    /// те вызовы, ради которых написана.
    /// </summary>
    private static IEnumerable<string> CallsTo(string text, string marker)
    {
        var from = 0;
        while (true)
        {
            var at = text.IndexOf(marker, from, StringComparison.Ordinal);
            if (at < 0) yield break;

            var start = at + marker.Length;
            var end = EndOfCall(text, start);
            yield return text[start..end];
            from = end;
        }
    }

    /// <summary>
    /// Конец списка аргументов. Скобки внутри строковых и символьных литералов и внутри
    /// комментариев НЕ считаются: закрывающая скобка в обычном русском перечислении («шаг 1) не
    /// удался: {ex.Message}») обрывала разбор ДО утечки, и проверка молчала именно там, где должна
    /// была кричать. Выражения в дырках интерполяции всегда сбалансированы, поэтому пропуск
    /// литерала целиком конец вызова не сдвигает.
    /// </summary>
    private static int EndOfCall(string text, int start)
    {
        var depth = 1;
        var i = start;
        while (i < text.Length && depth > 0)
        {
            var c = text[i];
            if (c is '"' or '\'') { i = SkipLiteral(text, i); continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] is '/' or '*') { i = SkipComment(text, i); continue; }

            if (c == '(') depth++;
            else if (c == ')') depth--;
            i++;
        }
        return depth == 0 ? i - 1 : text.Length;
    }

    /// <summary>Индекс сразу за литералом, начинающимся в <paramref name="i" /> (обычный, дословный
    /// <c>@"…"</c>, интерполированный и сырой <c>"""…"""</c>).</summary>
    private static int SkipLiteral(string text, int i)
    {
        if (text[i] == '\'')
        {
            i++;
            while (i < text.Length && text[i] != '\'') i += text[i] == '\\' ? 2 : 1;
            return i + 1;
        }

        var run = 0;
        while (i + run < text.Length && text[i + run] == '"') run++;
        if (run >= 3)
        {
            var end = text.IndexOf(new string('"', run), i + run, StringComparison.Ordinal);
            return end < 0 ? text.Length : end + run;
        }

        var verbatim = i > 0 && (text[i - 1] == '@' || (i > 1 && text[i - 1] == '$' && text[i - 2] == '@'));
        i++;
        while (i < text.Length)
        {
            if (text[i] == '\\' && !verbatim) { i += 2; continue; }
            if (text[i] == '"')
            {
                if (verbatim && i + 1 < text.Length && text[i + 1] == '"') { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return text.Length;
    }

    /// <summary>Индекс сразу за комментарием, начинающимся в <paramref name="i" />.</summary>
    private static int SkipComment(string text, int i)
    {
        if (text[i + 1] == '/')
        {
            var eol = text.IndexOf('\n', i);
            return eol < 0 ? text.Length : eol + 1;
        }
        var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
        return close < 0 ? text.Length : close + 2;
    }

    // Обращение к .Message у переменной исключения. Refusals.TextOr(ex, …) под правило не попадает
    // и потому остаётся единственной разрешённой формой.
    private static readonly Regex MessageAccess = new(
        @"\b(ex|e|error|exception)\w*\.Message\b", RegexOptions.Compiled | RegexOptions.Singleline);

    // Имена, связанные catch'ами: `catch (Exception io)` → io. Одной привычки называть переменную
    // ex недостаточно — сторож обязан ловить и утечку, написанную любым другим именем.
    private static readonly Regex CatchVariable = new(
        @"catch\s*\(\s*[\w.<>?]+\s+(\w+)\s*\)", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Проверка «в тексте есть сообщение чужого исключения» для одного файла: общая
    /// привычка именования плюс имена, которые этот файл связал своими catch'ами.</summary>
    private static Regex MessageAccessIn(string text)
    {
        var names = CatchVariable.Matches(text).Select(m => m.Groups[1].Value)
            .Where(n => !MessageAccess.IsMatch(n + ".Message"))
            .Distinct(StringComparer.Ordinal).ToList();
        return names.Count == 0
            ? MessageAccess
            : new Regex(MessageAccess.ToString() + @"|\b(" + string.Join("|", names.Select(Regex.Escape)) + @")\.Message\b",
                RegexOptions.Singleline);
    }

    private static string Shorten(string call)
    {
        var flat = Regex.Replace(call, @"\s+", " ").Trim();
        return flat.Length <= 120 ? flat : flat[..120] + "…";
    }

    private static string SolutionDir => SourceTree.SolutionDir;
}
