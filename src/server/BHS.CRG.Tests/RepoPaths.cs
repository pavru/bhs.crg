namespace BHS.CRG.Tests;

/// <summary>
/// Корень репозитория для тестов, которые читают его файлы: правила агентов, базовые уровни храповиков,
/// общие с клиентом примеры. Признак корня — <c>CLAUDE.md</c>: решение лежит в <c>src/server</c>, а тест
/// запускается из <c>bin</c> — двумя и более уровнями ниже.
///
/// <para>Помощник один (ревью PR #1508): копии в классах тестов расходились бы признаком и сообщением.</para>
/// </summary>
internal static class RepoPaths
{
    public static string Root { get; } = Find();

    /// <summary>Путь к файлу репозитория по частям от корня: <c>RepoPaths.File("src", "client", …)</c>.</summary>
    public static string File(params string[] parts) => Path.Combine([Root, .. parts]);

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Не найден корень репозитория (CLAUDE.md) выше " + AppContext.BaseDirectory +
                " — тест читает файлы репозитория, и без них сверять нечего.");
    }
}
