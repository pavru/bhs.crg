namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// <c>AGENTS.md</c> обязан быть дословной копией тела <c>CLAUDE.md</c>.
///
/// Файл с правилами один — <c>CLAUDE.md</c>; <c>AGENTS.md</c> существует ради агентов, которые
/// читают только его. Два файла с правилами расходятся молча, и узнают об этом тогда, когда агенты
/// начнут делать разное — по разным правилам, каждое из которых где-то записано.
///
/// ⚠️ Сторож заведён не «на всякий случай». Копия разошлась на СЛЕДУЮЩЕМ ЖЕ PR после того, как была
/// заведена: правка уехала в <c>CLAUDE.md</c>, в копию её никто не перенёс, и предупреждение в
/// шапке самого файла этому не помешало ничем.
///
/// Починка: перенести тело заново — всё от <see cref="Anchor" /> и до конца.
/// </summary>
public class AgentRulesCopyTests
{
    /// <summary>Начало общего тела. Выше него у каждого файла своя шапка, и это нормально.</summary>
    private const string Anchor = "## Что это за проект";

    [Fact]
    public void AgentsMd_IsVerbatimCopyOfClaudeMd()
    {
        var claude = Read("CLAUDE.md");
        var agents = Read("AGENTS.md");

        Assert.Contains(Anchor, claude);
        Assert.Contains(Anchor, agents);

        var body = claude[claude.IndexOf(Anchor, StringComparison.Ordinal)..];

        Assert.True(agents.EndsWith(body, StringComparison.Ordinal),
            "AGENTS.md разошёлся с CLAUDE.md. Это не оформление: агенты, читающие AGENTS.md, " +
            "работают по устаревшим правилам и об этом не узнают. Перенесите тело CLAUDE.md " +
            $"(всё от «{Anchor}») в AGENTS.md ниже его шапки — тем же коммитом, что и правку.");
    }

    private static string Read(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot, name)).ReplaceLineEndings("\n");

    /// <summary>Корень репозитория: решение лежит в <c>src/server</c>, правила — двумя уровнями выше.</summary>
    private static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Не найден корень репозитория (CLAUDE.md) выше " + AppContext.BaseDirectory +
                " — тест сверяет два файла правил, и без них сверять нечего.");
    }
}
