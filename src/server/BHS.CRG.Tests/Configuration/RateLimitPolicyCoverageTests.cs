using System.Text.RegularExpressions;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Имена политик ограничения частоты, заведённые приложением, обязаны быть известны тестовой
/// фикстуре (ревью PR #1061).
///
/// <para>Зачем сторож. Фикстура подменяет ограничитель целиком — ей нужны быстрые прогоны, а не
/// пределы, — и перечисляет имена политик СВОИМ списком. Список этот дублирует приложение, и
/// расходится он молча: новая политика на адресе даёт 500 «no such policy exists» в каждом тесте,
/// который этот адрес трогает, а сообщение указывает на фикстуру только тому, кто уже знает, где
/// искать. Мы на это и наступили, заведя политику <c>branding</c>.</para>
///
/// <para>Проверяется текст исходников, а не собранные опции: карта политик у
/// <c>RateLimiterOptions</c> внутренняя, и добраться до неё можно только отражением по приватному
/// имени — сторож ломался бы от смены версии фреймворка, ничего при этом не охраняя.</para>
/// </summary>
public class RateLimitPolicyCoverageTests
{
    private static readonly Regex AddPolicy = new(
        """AddPolicy\(\s*"(?<name>[^"]+)"\s*,""", RegexOptions.Compiled);

    [Fact]
    public void Каждая_политика_приложения_известна_фикстуре()
    {
        var app = PolicyNamesIn("BHS.CRG.Api/Configuration/ServiceRegistration.Host.cs");
        var fixture = FixturePolicyNames();

        var missing = app.Except(fixture).Order().ToList();

        Assert.True(missing.Count == 0,
            "Политики ограничения частоты, о которых не знает IntegrationTestFixture:\n  "
            + string.Join("\n  ", missing)
            + "\n\nАдрес с такой политикой отвечает 500 «no such policy exists» в каждом "
            + "интеграционном тесте, который его трогает. Добавьте имя в список фикстуры — там же "
            + "объяснено, почему политики подменяются, а не переиспользуются.");
    }

    /// <summary>
    /// Обратная половина: имя, оставшееся в фикстуре без политики в приложении, — такой же
    /// разошедшийся список, только тихий. Он ничего не ломает и потому живёт годами, обещая
    /// покрытие, которого нет.
    /// </summary>
    [Fact]
    public void И_наоборот_лишних_имён_в_фикстуре_нет()
    {
        var app = PolicyNamesIn("BHS.CRG.Api/Configuration/ServiceRegistration.Host.cs");
        var stale = FixturePolicyNames().Except(app).Order().ToList();

        Assert.True(stale.Count == 0,
            "Имена в списке фикстуры, которым в приложении больше не отвечает ни одна политика:\n  "
            + string.Join("\n  ", stale));
    }

    private static HashSet<string> PolicyNamesIn(string relativePath) =>
        [.. AddPolicy.Matches(File.ReadAllText(Path.Combine(SolutionDir, relativePath)))
            .Select(m => m.Groups["name"].Value)];

    /// <summary>
    /// Имена из строки вида <c>(string[])["login", "auth", …]</c> в фикстуре. Разбор текстом — по
    /// той же причине, что и у приложения: сама фикстура собирается в другом процессе, и спросить у
    /// неё список во время этого теста нельзя.
    /// </summary>
    private static HashSet<string> FixturePolicyNames()
    {
        var text = File.ReadAllText(Path.Combine(SolutionDir, "BHS.CRG.Tests/Integration/IntegrationTestFixture.cs"));
        var line = Regex.Match(text, """foreach \(var policy in \(string\[\]\)\[(?<names>[^\]]+)\]""");
        Assert.True(line.Success,
            "В IntegrationTestFixture не найден список имён политик — сторож потерял то, что "
            + "охраняет, и молчать ему нельзя: перепишите разбор под новую форму списка.");
        return [.. Regex.Matches(line.Groups["names"].Value, "\"(?<name>[^\"]+)\"")
            .Select(m => m.Groups["name"].Value)];
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
