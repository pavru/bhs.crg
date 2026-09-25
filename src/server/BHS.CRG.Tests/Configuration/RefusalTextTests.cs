using System.Text.RegularExpressions;
using BHS.CRG.Application.Generation;
using BHS.CRG.Infrastructure.Generation;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Две двери, через которые чужое сообщение уходило наружу МИМО правила #691 (issue #1050).
///
/// <para>Правило: дословно наружу идёт только текст нашего отказа, чужое заменяется общим текстом, а
/// подробности — в журнал. У ответа на запрос его держит конвейер (<see cref="ApiErrorMappingTests" />),
/// у типа отказа — <see cref="DomainExceptionPolicyTests" />, у колокольчика —
/// <see cref="NotificationRefusalTextTests" />. Оставались две формы, которые все три проверки
/// проходят честно и правило всё равно обходят — потому что обходят его ПО ПОСТРОЕНИЮ.</para>
///
/// <para><b>Первая.</b> <c>throw new InvalidRequestException($"…: {ex.Message}")</c>. Тип наш, значит
/// конвейер отдаёт текст дословно — а собран он наполовину из чужого сообщения. Снаружи это выглядит
/// заботой о пользователе, и ни компилятор, ни прежние сторожа возразить не могут: тип правильный.</para>
///
/// <para><b>Вторая.</b> Ответ эндпоинта, собранный в ОБЩЕМ перехвате: <c>catch (Exception ex)</c> →
/// <c>new { error = ex.Message }</c>. Конвейер сюда не заглядывает вовсе — исключение до него не
/// доходит. Типизированные перехваты (<c>catch (ConflictException ex)</c>) правилу не противоречат:
/// там тип и есть признак нашего отказа, и проверка их не трогает.</para>
///
/// <para>Третья проверка — про обещание, на которое опираются первые две. Отказы движков
/// распознавания и поиска доходят до человека дословно (<c>EngineRefusal.TextOf</c>), и это решение:
/// «Не задан ключ Gemini» и «достигнут лимит запросов» — то, ради чего экран и открывали. Решение
/// держится тем, что тексты этого семейства собираются только из наших слов. Собираются они в
/// движках, где рядом лежат тело чужого ответа и сообщение чужого исключения, — поэтому обещание
/// проверяется, а не заявляется.</para>
/// </summary>
public class RefusalTextTests
{
    private static readonly string[] Projects =
        ["BHS.CRG.Domain", "BHS.CRG.Application", "BHS.CRG.Infrastructure", "BHS.CRG.Api"];

    /// <summary>
    /// Все потомки <see cref="DomainException" /> — ОТРАЖЕНИЕМ, а не списком имён.
    ///
    /// <para>Список имён здесь был бы дырой, и притом приглашённой: <c>DomainException</c> прямо
    /// зовёт заводить свои роды («Роды отказа не запечатаны: свой тип с говорящим именем
    /// наследуется от подходящего рода»), и так уже сделаны <c>TemplateCompilationException</c> и
    /// <c>TypstTimeoutException</c>. Первый же следующий такой тип с <c>{ex.Message}</c> внутри
    /// прошёл бы проверку молча — ровно тем способом, ради закрытия которого она написана.</para>
    ///
    /// <para>Сборки называются типом из каждой, а не берутся из <c>AppDomain</c>: та отдаёт лишь
    /// ЗАГРУЖЕННЫЕ, а .NET грузит сборку по первому обращению — проверка молча обеднела бы в
    /// зависимости от того, какие тесты успели отработать раньше.</para>
    /// </summary>
    private static readonly string[] RefusalTypeNames =
        new[]
        {
            typeof(DomainException).Assembly,
            typeof(TemplateCompilationException).Assembly,
            typeof(TypstTimeoutException).Assembly,
        }
        .Distinct()
        .SelectMany(a => a.GetTypes())
        .Where(t => !t.IsAbstract && typeof(DomainException).IsAssignableFrom(t))
        .Select(t => t.Name)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(n => n, StringComparer.Ordinal)
        .ToArray();

    /// <summary>Роды нашего отказа: их текст конвейер отдаёт клиенту дословно.</summary>
    private static readonly Regex DomainThrow = new(
        @"throw\s+new\s+(" + string.Join("|", RefusalTypeNames) + @")\s*\(",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Семейство отказов движков: текст уходит наружу через <c>EngineRefusal.TextOf</c>.</summary>
    private static readonly Regex EngineThrow = new(
        @"throw\s+new\s+(Recognition\w*Exception|SearchUnavailableException)\s*\(",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Обращение к сообщению исключения. <c>Policy.RefusalMessage</c> сюда не попадает.</summary>
    private static readonly Regex ForeignMessage = new(
        @"\w*\.Message\b|\bex\w*\.ToString\s*\(", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Чужой ответ целиком: тело запроса и его усечение. В текст отказа не идут.</summary>
    private static readonly Regex ForeignBody = new(
        @"\bTruncate\s*\(|(?<![A-Za-z0-9_.])body\b", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Формы, которым сообщение исключения доверено НАЗВАННО, — вырезаются перед проверкой.</summary>
    private static readonly Regex Sanctioned = new(
        @"(Refusals\.TextOr|EngineRefusal\.TextOf|OutboundDiagnosis\.(Describe|Mask))\s*\([^()]*(\([^()]*\)[^()]*)*\)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// Осознанные исключения ПЕРВОЙ проверки. Ключ — сам вызов, сплющенный в строку, а не имя файла:
    /// правка текста обязана вернуть решение на пересмотр, а имя файла её не заметит. Список
    /// короткий и должен таким оставаться — каждая строка здесь это сообщение, уходящее наружу.
    /// </summary>
    private static readonly Dictionary<string, string> Deliberate = new(StringComparer.Ordinal)
    {
        ["$\"Выражение не разбирается: {ex.Message}\", ex"] =
            "разборщик ВЫРАЖЕНИЯ (XPath/JSONPath/XML): сообщение про то, что написал сам пользователь — "
            + "где оборвалась скобка, какой символ не на месте. Общий текст оставил бы построитель без "
            + "диагностики; типы разбора названы в catch поимённо, а ArgumentException и "
            + "InvalidOperationException ловятся отдельной веткой и уходят в inner",
    };

    [Fact]
    public void Наш_отказ_не_собирается_из_чужого_сообщения()
    {
        // Сначала — что проверять вообще есть чем. Отражение обеднеет тихо (переименован базовый
        // тип, не та сборка), и тогда регулярное выражение перестанет совпадать хоть с чем-нибудь,
        // а проверка останется зелёной — отказ, переодетый в успех. Сверяются четыре рода из
        // BHS.CRG.Domain: они объявлены в одном файле с базовым типом и исчезнуть порознь не могут.
        // Две другие сборки названы typeof-ами, то есть сверены компилятором.
        string[] baseKinds =
            ["InvalidRequestException", "NotFoundException", "ConflictException", "ForbiddenException"];
        Assert.True(baseKinds.All(k => RefusalTypeNames.Contains(k, StringComparer.Ordinal)),
            "Роды отказа собраны отражением, и среди них нет базовых. Собрано: " +
            string.Join(", ", RefusalTypeNames) + ". Значит базовый тип переименован или сборка " +
            "названа не та, и проверка ниже не смотрит ни на что.");

        var offenders = new List<string>();

        foreach (var (file, text) in Sources(Projects))
            foreach (var (call, line) in Calls(text, DomainThrow))
            {
                if (!ForeignMessage.IsMatch(call)) continue;
                if (Deliberate.ContainsKey(Flat(call))) continue;
                offenders.Add($"{Path.GetFileName(file)}:{line}  {Short(call)}");
            }

        Assert.True(offenders.Count == 0,
            "Наш тип отказа несёт чужое сообщение:\n  " + string.Join("\n  ", offenders) + "\n\n" +
            "Тип наш — значит конвейер отдаёт этот текст пользователю дословно, вместе с чужой\n" +
            "половиной. Так наружу уезжают сообщения разборщика PDF, SDK хранилища, Npgsql и Jint.\n" +
            "Оставьте свой текст, а исключение передайте вторым аргументом (inner) — целиком его\n" +
            "запишет конвейер. Если внутри отказ движка и его текст нужен человеку целиком, возьмите\n" +
            "EngineRefusal.TextOf(ex): это названная форма, и у неё есть своя проверка. Если\n" +
            "сообщение заведомо наше по другой причине — впишите вызов в Deliberate с причиной.");
    }

    [Fact]
    public void Отказ_движка_не_вбирает_чужой_текст()
    {
        var offenders = new List<string>();

        foreach (var (file, text) in Sources(Projects))
            foreach (var (call, line) in Calls(text, EngineThrow))
            {
                if (!ForeignMessage.IsMatch(call) && !ForeignBody.IsMatch(call)) continue;
                offenders.Add($"{Path.GetFileName(file)}:{line}  {Short(call)}");
            }

        Assert.True(offenders.Count == 0,
            "Отказ движка вбирает чужой текст:\n  " + string.Join("\n  ", offenders) + "\n\n" +
            "На тексты этого семейства опирается EngineRefusal.TextOf: они уходят человеку дословно —\n" +
            "в ответ эндпоинта библиотеки качества и в отказ распознавания наборов данных. Значит\n" +
            "собирать их можно только из наших слов: литералов, наших же настроек (адрес движка, имя\n" +
            "модели, срок ответа) и разбора OutboundDiagnosis.Describe/Mask, который прячет логин с\n" +
            "паролем. Тело чужого ответа и сообщение чужого исключения — в журнал: рядом уже стоит\n" +
            "logger, а полезное из тела извлекает ModelGone.AdviceFrom нашими словами.");
    }

    [Fact]
    public void Ответ_эндпоинта_из_общего_перехвата_не_несёт_чужого_сообщения()
    {
        var offenders = new List<string>();
        string[] roots =
        [
            Path.Combine("BHS.CRG.Api", "Endpoints"),
            Path.Combine("BHS.CRG.Api", "Mcp"),
        ];

        foreach (var (file, text) in Sources(roots))
            foreach (var (body, line) in GeneralCatchBodies(text))
            {
                if (!ForeignMessage.IsMatch(Sanctioned.Replace(body, string.Empty))) continue;
                offenders.Add($"{Path.GetFileName(file)}:{line}  {Short(body)}");
            }

        Assert.True(offenders.Count == 0,
            "Ответ эндпоинта несёт сообщение чужого исключения:\n  " + string.Join("\n  ", offenders) + "\n\n" +
            "Общий перехват ловит ВСЁ, и конвейер такого исключения уже не увидит: ответ сформирован\n" +
            "здесь. Так наружу уходят текст MailKit с почтовым сервером и учётной записью, вывод\n" +
            "запуска Typst с путями установки, сообщения Npgsql и SDK хранилища. Напишите свой текст,\n" +
            "а исключение положите в журнал (ILoggerFactory в параметрах обработчика). Нужен разбор\n" +
            "для администратора — берите OutboundDiagnosis.Describe: он объясняет, чей это отказ, и\n" +
            "прячет логин с паролем. Перехват по ТИПУ отказа (catch (ConflictException ex) и\n" +
            "catch (Exception ex) when (ex is …)) правилу не противоречит, и проверка его не трогает.");
    }

    // ── Разбор исходника ────────────────────────────────────────────────────────

    /// <summary>Все .cs проекта или подкаталога, кроме obj/bin.</summary>
    private static IEnumerable<(string File, string Text)> Sources(IEnumerable<string> relativeRoots)
    {
        foreach (var relative in relativeRoots)
        {
            var root = Path.Combine(SolutionDir, relative);
            if (!Directory.Exists(root)) continue;

            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    continue;
                yield return (file, File.ReadAllText(file));
            }
        }
    }

    /// <summary>
    /// Аргументы каждого вызова, найденного <paramref name="marker" />, — по балансу скобок.
    /// Построчная проверка здесь не годится: длинный текст отказа переносят, и ровно те вызовы,
    /// ради которых написана проверка, она бы и не увидела.
    /// </summary>
    private static IEnumerable<(string Call, int Line)> Calls(string text, Regex marker)
    {
        foreach (Match m in marker.Matches(text))
            yield return (Balanced(text, m.Index + m.Length, '(', ')'), LineAt(text, m.Index));
    }

    /// <summary>
    /// Тела ОБЩИХ перехватов: <c>catch (Exception …)</c> и <c>catch { }</c> без фильтра по типу.
    /// Фильтр вида <c>when (ex is Foo or Bar)</c> делает перехват типизированным — такие пропускаем;
    /// <c>when (ex is not …)</c> типизированным не делает, он лишь исключает один случай.
    /// </summary>
    private static IEnumerable<(string Body, int Line)> GeneralCatchBodies(string text)
    {
        foreach (Match m in GeneralCatch.Matches(text))
        {
            var brace = text.IndexOf('{', m.Index + m.Length - 1);
            if (brace < 0) continue;

            var header = text[m.Index..brace];
            if (header.Contains(" is ", StringComparison.Ordinal)
                && !header.Contains(" is not ", StringComparison.Ordinal))
                continue;

            yield return (Balanced(text, brace + 1, '{', '}'), LineAt(text, m.Index));
        }
    }

    private static readonly Regex GeneralCatch = new(
        @"catch\s*(\(\s*(System\.)?Exception\b[^)]*\)\s*(when\s*\([^{]*\))?\s*)?\{",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Содержимое от <paramref name="from" /> до закрывающей скобки нужного уровня.</summary>
    private static string Balanced(string text, int from, char open, char close)
    {
        var depth = 1;
        var i = from;
        while (i < text.Length && depth > 0)
        {
            if (text[i] == open) depth++;
            else if (text[i] == close) depth--;
            i++;
        }
        return text[from..(depth == 0 ? i - 1 : text.Length)];
    }

    private static int LineAt(string text, int index) => text[..index].Count(c => c == '\n') + 1;

    private static string Flat(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private static string Short(string s)
    {
        var flat = Flat(s);
        return flat.Length <= 140 ? flat : flat[..140] + "…";
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
