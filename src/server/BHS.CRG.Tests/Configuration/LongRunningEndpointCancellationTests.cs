using System.Text.RegularExpressions;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Долгий эндпоинт обязан принимать <c>CancellationToken</c> и передавать его в MediatR (issue #797).
///
/// Тест написан против конкретной ловушки, а не ради стройности. Minimal API отдаёт токен только
/// тому, кто его попросил параметром; не попросив, обработчик получает <c>default</c> — и любая
/// проверка «пользователь ушёл» внутри становится мёртвой веткой. Мёртвой ТИХО: код читается как
/// рабочий, компилятор доволен, тесты обработчика зелёные, потому что токен им передают напрямую.
///
/// Ровно это и случилось: в обработчике распознавания появилась ветка «отмену не считаем ошибкой»,
/// а эндпоинт токен не принимал — ветка не могла сработать ни разу. Цена не в самой ветке:
/// распознавание идёт минутами, и без токена брошенный запрос продолжает жечь квоту облачного
/// движка и дочитывать страницы, которые уже некому показать.
///
/// Проверяем ИСХОДНИКИ: связь «параметр в лямбде» → «аргумент в Send» в метаданных не отражается.
/// </summary>
public class LongRunningEndpointCancellationTests
{
    /// <summary>
    /// Эндпоинты, которые идут долго и работают с внешними службами. Список — осознанное решение:
    /// добавляя сюда маршрут, вы говорите «этот запрос имеет смысл обрывать».
    /// </summary>
    private static readonly (string File, string Route, string Why)[] MustAcceptToken =
    [
        ("BHS.CRG.Api/Endpoints/QualityDocs/QualityDocEndpoints.cs", "/recognize",
            "распознавание идёт минутами через внешний vision-движок"),
        ("BHS.CRG.Api/Endpoints/QualityDocs/QualityDocEndpoints.cs", "/search",
            "веб-поиск раскрывает найденные страницы по одной"),
    ];

    [Fact]
    public void LongRunningEndpoints_AcceptCancellationToken()
    {
        var missing = new List<string>();
        foreach (var (file, route, why) in MustAcceptToken)
        {
            var lambda = EndpointLambda(file, route);
            if (!lambda.Contains("CancellationToken", StringComparison.Ordinal))
                missing.Add($"{route} ({why}) — в сигнатуре обработчика нет CancellationToken");
        }
        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    [Fact]
    public void LongRunningEndpoints_PassTokenToMediator()
    {
        // Принять токен и не передать его — та же мёртвая ветка, только выглядит ещё убедительнее.
        var missing = new List<string>();
        foreach (var (file, route, why) in MustAcceptToken)
        {
            var lambda = EndpointLambda(file, route);
            // Границу вызова берём по `;`, а не по первой `)`: аргумент сам содержит скобки
            // (`m.Send(new SearchQualityDocsQuery(req.Query), ct)`), и разбор по скобкам обрезал бы
            // вызов на середине — проверка не нашла бы токен, который на месте.
            foreach (Match send in Regex.Matches(lambda, @"m\.Send\([^;]*?;", RegexOptions.Singleline))
                if (!Regex.IsMatch(send.Value, @",\s*ct\s*\)", RegexOptions.Singleline))
                    missing.Add($"{route} ({why}) — вызов m.Send без токена: {Compact(send.Value)}");
        }
        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    /// <summary>Текст обработчика маршрута: от MapPost до начала следующей регистрации.</summary>
    private static string EndpointLambda(string file, string route)
    {
        var src = File.ReadAllText(Path.Combine(SolutionDir, file));
        var start = src.IndexOf($"MapPost(\"{route}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"В {file} не найдена регистрация MapPost(\"{route}\") — маршрут переименован или переехал.");
        var next = src.IndexOf("        g.Map", start + 1, StringComparison.Ordinal);
        return next > start ? src[start..next] : src[start..];
    }

    private static string Compact(string s) => Regex.Replace(s, @"\s+", " ").Trim();

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
