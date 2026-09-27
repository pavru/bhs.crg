using System.Text.RegularExpressions;

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
/// ответ хранилища с именем бакета — прямо в таблицу на экране. Двери закрывались ПООДИНОЧКЕ:
/// следующий вызов напишут так же, и компилятор не возразит. Поэтому проверка смотрит на форму
/// вызова, а не на нынешний состав.</para>
///
/// <para>Санкционированная форма — <c>Refusals.TextOr(ex, «общий текст»)</c>: она и отделяет наш
/// отказ от чужого, и заставляет автора придумать этот общий текст.</para>
/// </summary>
public class NotificationRefusalTextTests
{
    private static readonly string[] Projects =
        ["BHS.CRG.Api", "BHS.CRG.Application", "BHS.CRG.Infrastructure"];

    /// <summary>
    /// Осознанные исключения: имя файла → причина. Пустой список — не признак, что правило лишнее:
    /// он ровно то, к чему стремились. Добавляя строку, вы принимаете решение.
    /// </summary>
    private static readonly Dictionary<string, string> NotificationDeliberate = new(StringComparer.Ordinal);

    /// <inheritdoc cref="NotificationDeliberate" />
    private static readonly Dictionary<string, string> PreviewDeliberate = new(StringComparer.Ordinal)
    {
        ["ComputedFieldResolver.cs"] =
            "Ошибка расчётного поля: сообщение движка формул — единственный отклик автору выражения, " +
            "и говорит оно ровно о его выражении («x is not defined»). Чужому тексту здесь взяться " +
            "неоткуда: вычисление идёт над словарём уже собранных значений — без базы, хранилища и " +
            "сети, — так что споткнуться можно только о саму формулу.",
    };

    [Fact]
    public void A_notification_never_carries_a_foreign_exception_message()
        => AssertNoForeignMessage(
            ["PublishAsync("], NotificationDeliberate,
            "Уведомление несёт сообщение чужого исключения:",
            "Через колокольчик так уезжают вывод компилятора шаблона с путями папки прогона, текст\n" +
            "Npgsql со строкой подключения и ответы сторонних сервисов вместе с их адресами.");

    /// <summary>
    /// Те же проверки — конструкторам DTO, через которые текст ошибки попадает человеку на экран
    /// (issue #1059): предпросмотр материализации источника, экран «Проверка связок» и диагностика
    /// генерации. Выбраны не «все DTO подряд», а ровно те, у которых есть поле с текстом ошибки и
    /// которые собираются в <c>catch</c>: именно там соблазн отдать <c>ex.Message</c> максимален —
    /// иначе причина как будто исчезает совсем.
    /// </summary>
    [Fact]
    public void A_preview_shown_to_a_person_never_carries_a_foreign_exception_message()
        => AssertNoForeignMessage(
            ["MaterializePreviewDto(", "BindingPreviewDto(", "ResolutionDiagnostic("], PreviewDeliberate,
            "Строка ошибки, показанная человеку, несёт сообщение чужого исключения:",
            "Эти DTO собираются в catch вокруг чтения источника, то есть в их поле ошибки уезжают\n" +
            "текст Npgsql со строкой подключения, ответ хранилища с именем бакета и ошибки движка\n" +
            "скриптов — и показываются в таблице прямо на экране.");

    private static void AssertNoForeignMessage(
        string[] markers, Dictionary<string, string> deliberate, string headline, string what)
    {
        var offenders = new List<string>();
        var usedExemptions = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in Projects)
        {
            var root = Path.Combine(SolutionDir, project);
            if (!Directory.Exists(root)) continue;

            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    continue;

                var name = Path.GetFileName(file);
                var text = File.ReadAllText(file);
                var exempt = deliberate.ContainsKey(name);

                foreach (var marker in markers)
                    foreach (var call in CallsTo(text, marker))
                    {
                        if (!MessageAccess.IsMatch(call)) continue;
                        // Сработавшее исключение отмечаем ДО отсева: иначе закрытая дверь оставила бы
                        // в списке строку, которая уже ничего не разрешает, — и следующая такая же
                        // ошибка в этом файле прошла бы молча, прикрывшись чужой причиной.
                        if (exempt) usedExemptions.Add(name);
                        else offenders.Add($"{name}: {Shorten(call)}");
                    }
            }
        }

        Assert.True(offenders.Count == 0,
            headline + "\n  " + string.Join("\n  ", offenders) + "\n\n" + what + "\n" +
            "Оберните текст в Refusals.TextOr(ex, «общий текст»): наш отказ дойдёт дословно, чужой —\n" +
            "заменится, а исключение целиком запишет журнал рядом. Если сообщение здесь заведомо\n" +
            "наше, впишите файл в список осознанных исключений с причиной — это решение, а не\n" +
            "формальность.");

        var stale = deliberate.Keys.Except(usedExemptions).ToList();
        Assert.True(stale.Count == 0,
            "Исключение из правила больше ничего не разрешает: " + string.Join(", ", stale) + "\n" +
            "Дверь закрыли, а строку не убрали — и следующий ex.Message в этом файле пройдёт молча,\n" +
            "прикрывшись чужой причиной. Удалите строку из списка.");
    }

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
            var depth = 1;
            var i = start;
            while (i < text.Length && depth > 0)
            {
                if (text[i] == '(') depth++;
                else if (text[i] == ')') depth--;
                i++;
            }

            yield return text[start..(depth == 0 ? i - 1 : text.Length)];
            from = i;
        }
    }

    // Обращение к .Message у переменной исключения. Refusals.TextOr(ex, …) под правило не попадает
    // и потому остаётся единственной разрешённой формой.
    private static readonly Regex MessageAccess = new(
        @"\b(ex|e|error|exception)\w*\.Message\b", RegexOptions.Compiled | RegexOptions.Singleline);

    private static string Shorten(string call)
    {
        var flat = Regex.Replace(call, @"\s+", " ").Trim();
        return flat.Length <= 120 ? flat : flat[..120] + "…";
    }

    private static string SolutionDir { get; } = FindSolutionDir();

    private static string FindSolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BHS.CRG.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Не найден каталог решения (BHS.CRG.slnx) выше " + AppContext.BaseDirectory +
                " — тест читает исходники и без них проверять нечего.");
    }
}
