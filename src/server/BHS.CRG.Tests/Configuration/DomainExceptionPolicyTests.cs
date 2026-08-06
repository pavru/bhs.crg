using System.Text.RegularExpressions;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Отказ, адресованный пользователю, бросается доменным типом — и никак иначе (issue #691).
///
/// Тест написан против дрейфа, а не ради нынешнего состава. Написать
/// <c>throw new ArgumentException("Укажите название")</c> по-прежнему можно, и компилятор не
/// возразит; изменится только результат — пользователь увидит «Внутренняя ошибка сервера»
/// вместо подсказки. Это не падение и не исключение в логе: отказ просто станет невнятным, и
/// заметят это не скоро.
///
/// Обратная сторона правила — та, ради которой всё затевалось: framework-типы бросают ещё и
/// Npgsql, SDK хранилища, Jint и разбор CIDR, и их сообщения называют хост, базу, бакет и путь.
/// Пока наши отказы неотличимы от чужих по типу, выбор «показать или скрыть» сделать нечем.
///
/// Приём тот же, что у <see cref="Integration.FixtureResetCoverageTests" />: связать «написал
/// отказ» с «прими решение», пока решение ещё дёшево.
/// </summary>
public class DomainExceptionPolicyTests
{
    /// <summary>Проекты, где живут отказы пользователю. Api сюда не входит: см. <see cref="ApiOnly" />.</summary>
    private static readonly string[] Projects = ["BHS.CRG.Domain", "BHS.CRG.Application", "BHS.CRG.Infrastructure"];

    // \s+ между словами, а не пробел: длинный throw переносят на следующую строку, и построчная
    // проверка такой пропустила бы — то есть обойти правило можно было бы, просто нажав Enter.
    private static readonly Regex FrameworkThrow = new(
        @"\bthrow\s+new\s+(ArgumentException|ArgumentNullException|ArgumentOutOfRangeException|KeyNotFoundException|InvalidOperationException|NotSupportedException)\b",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// Внутренние инварианты и отказы конфигурации: их текст пользователю не адресован, и уходить он
    /// должен в лог, а не на экран. Добавляя строку, вы принимаете решение — именно этого тест и
    /// добивается.
    /// </summary>
    private static readonly Dictionary<string, string> DeliberatelyFramework = new()
    {
        ["BHS.CRG.Infrastructure/Generation/TypstGenerator.cs"] = "сбой компилятора Typst: stderr с путями временной папки — диагностика для лога",
        ["BHS.CRG.Infrastructure/Generation/TypstProcess.cs"] = "Typst не запустился — это нерабочая конфигурация установки",
        ["BHS.CRG.Infrastructure/Generation/DocumentGeneratorFactory.cs"] = "формат вне перечисления — недостижимо снаружи",
        ["BHS.CRG.Infrastructure/Jobs/JobBackgroundService.cs"] = "неизвестный вид фоновой задачи — дефект реестра задач",
        ["BHS.CRG.Infrastructure/Recognition/BuiltInRecognitionProfiles.cs"] = "встроенный профиль отсутствует в коде — дефект",
        ["BHS.CRG.Infrastructure/Recognition/RecognitionKinds.cs"] = "вид профиля не описан в реестре — дефект",
        ["BHS.CRG.Infrastructure/Recognition/RecognitionProfileProvider.cs"] = "не сработал сидинг при старте — дефект развёртывания",
        ["BHS.CRG.Infrastructure/DataSets/DataSetParserFactory.cs"] = "нет парсера для формата — дефект реестра парсеров",
        ["BHS.CRG.Infrastructure/Generation/UserLibMaterializer.cs"] = "сработала защита от записи за пределы дерева — обязана быть громкой, а не тихим 409",
        ["BHS.CRG.Infrastructure/Storage/BlobStorage.cs"] = "путь берётся из базы, а не из запроса: «исправьте запрос» тут неверно по существу",
        ["BHS.CRG.Domain/Objects/DomainObject.cs"] = "документные свойства спросили у не-документа — дефект вызывающего кода",
        ["BHS.CRG.Infrastructure/Backup/CountingStream.cs"] = "счётчик байтов: чтения и перемотки у него нет по устройству, до пользователя это не доходит",
    };

    /// <summary>
    /// В Api доменных отказов не бросают вовсе: слой отвечает кодами, а не исключениями. Исключения
    /// здесь — только про сам процесс (не поднялся, не настроен), и они не про пользователя.
    /// </summary>
    private const string ApiOnly = "BHS.CRG.Api";

    [Fact]
    public void OurRefusals_AreThrownAsDomainExceptions()
    {
        var offenders = new List<string>();
        foreach (var project in Projects)
            foreach (var file in SourceFiles(project))
            {
                var rel = Relative(file);
                if (DeliberatelyFramework.ContainsKey(rel)) continue;
                var text = File.ReadAllText(file);
                foreach (Match m in FrameworkThrow.Matches(text))
                    offenders.Add($"{rel}:{LineOf(text, m.Index)}  throw new {m.Groups[1].Value}");
            }

        Assert.True(offenders.Count == 0,
            "Отказ брошен framework-типом — его текст пользователь не увидит, вместо него будет " +
            "«Внутренняя ошибка сервера»:\n" + string.Join("\n", offenders) + "\n\n" +
            "Если это отказ ПОЛЬЗОВАТЕЛЮ — возьмите InvalidRequestException / NotFoundException / " +
            "ConflictException / ForbiddenException. Если это внутренний инвариант — впишите файл в " +
            "DeliberatelyFramework с причиной.");
    }

    /// <summary>
    /// Имя в списке исключений, за которым больше нет ни одного такого throw, — след переезда.
    /// Оставленное, оно тихо разрешает следующему автору этого файла бросить что угодно.
    /// </summary>
    [Fact]
    public void ExemptionList_HasNoStaleEntries()
    {
        var stale = DeliberatelyFramework.Keys
            .Where(rel =>
            {
                var path = Path.Combine(SolutionDir, rel.Replace('/', Path.DirectorySeparatorChar));
                return !File.Exists(path) || !FrameworkThrow.IsMatch(File.ReadAllText(path));
            })
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(stale.Count == 0,
            "В списке исключений файлы, где framework-отказов больше нет: " + string.Join(", ", stale) +
            ".\nУберите строки — иначе список разрешает то, чего уже не делают.");
    }

    /// <summary>Слой API отвечает кодами; исключения здесь только про процесс, и их немного.</summary>
    [Fact]
    public void ApiLayer_DoesNotThrowDomainRefusals()
    {
        var domainThrow = new Regex(
            @"\bthrow\s+new\s+(InvalidRequestException|NotFoundException|ConflictException|ForbiddenException)\b",
            RegexOptions.Singleline);

        var offenders = SourceFiles(ApiOnly)
            .Select(f => (file: f, text: File.ReadAllText(f)))
            .SelectMany(x => domainThrow.Matches(x.text).Select(m => $"{Relative(x.file)}:{LineOf(x.text, m.Index)}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Доменный отказ брошен в слое API: " + string.Join(", ", offenders) + ".\n" +
            "Эндпоинт отвечает кодом (Results.BadRequest и соседи) — исключение здесь только " +
            "удлиняет путь до того же ответа.");
    }

    private static IEnumerable<string> SourceFiles(string project) =>
        Directory.EnumerateFiles(Path.Combine(SolutionDir, project), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <summary>Номер строки по смещению в тексте — сообщение должно вести прямо к месту.</summary>
    private static int LineOf(string text, int index) =>
        text.AsSpan(0, index).Count('\n') + 1;

    private static string Relative(string full) =>
        Path.GetRelativePath(SolutionDir, full).Replace('\\', '/');

    /// <summary>
    /// Каталог решения — от папки сборки вверх до файла решения. Тест читает ИСХОДНИКИ, а не
    /// сборку: правило про то, как написан код, отражения в метаданных не имеет.
    /// </summary>
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
