using System.Text.RegularExpressions;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Data;
using BHS.CRG.Tests.Integration;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Перепись обращений к закрытиям периода: набор трогает ОДНА служба (ТЗ CORE-35; задача E1a,
/// issue #1081). Приём <see cref="ActivityLogInventoryTests" />.
///
/// <para>Пометка «только дописывается» отвергает правку через отслеживание изменений, но
/// <c>db.PeriodClosures.ExecuteDelete()</c> идёт мимо него — компилируется, работает и стирает
/// решение о закрытии вместе с ответом на вопрос «кто и когда». Поймать такое можно только
/// перечислением мест, где набор вообще упоминается.</para>
///
/// <para>Заодно держится и второе: закрыть период в обход службы значит закрыть его без замка, без
/// проверки «подряд» и без записи в журнале.</para>
/// </summary>
public class PeriodClosureInventoryTests
{
    private static readonly string[] Projects = ["BHS.CRG.Api", "BHS.CRG.Application", "BHS.CRG.Infrastructure"];

    private static readonly Dictionary<string, string> MayTouchTheSet = new()
    {
        ["BHS.CRG.Infrastructure/Periods/PeriodClosureService.cs"] =
            "сама служба закрытия — единственный путь записи и чтения (ТЗ CORE-35)",
        ["BHS.CRG.Infrastructure/Persistence/AppDbContext.cs"] =
            "объявление набора; отказ на правку и удаление — там же, по пометке сущности",
        ["BHS.CRG.Infrastructure/Persistence/Configurations/PeriodClosureConfiguration.cs"] =
            "имя таблицы в настройке EF — объявление, а не обращение",
    };

    private static readonly Regex Set = new(@"\bPeriodClosures\b|\bperiod_closures\b", RegexOptions.Compiled);

    /// <summary>Упоминания, которые набором не являются: поле манифеста копии и её раздел.</summary>
    private static readonly Regex NotTheSet = new(
        @"manifest\.PeriodClosures|PeriodClosures:|BackupPeriodClosure\[\]\? PeriodClosures|RestorePeriodClosuresAsync",
        RegexOptions.Compiled);

    [Fact]
    public void К_набору_закрытий_обращается_только_служба()
    {
        var outsiders = new List<string>();

        foreach (var file in Projects.SelectMany(SourceFiles))
        {
            var rel = Relative(file);
            if (MayTouchTheSet.ContainsKey(rel)) continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("//")) continue;
                if (Set.IsMatch(NotTheSet.Replace(line, ""))) outsiders.Add($"{rel}:{i + 1}");
            }
        }

        Assert.True(outsiders.Count == 0,
            "К набору закрытий периода обратились мимо службы:\n" + string.Join("\n", outsiders) + "\n\n" +
            "Закрытия пишутся и читаются через IPeriodClosures: только там у записи есть автор, замок " +
            "против одновременной записи в учёт и проверка «подряд, без пропусков», и только там её " +
            "нельзя стереть. Если обращение всё-таки нужно — впишите файл в MayTouchTheSet с причиной.");
    }

    [Fact]
    public void Перепись_не_хранит_умерших_записей()
    {
        var stale = MayTouchTheSet.Keys
            .Where(rel =>
            {
                var path = Path.Combine(SolutionDir, rel.Replace('/', Path.DirectorySeparatorChar));
                return !File.Exists(path) || !Set.IsMatch(File.ReadAllText(path));
            })
            .ToList();

        Assert.True(stale.Count == 0,
            "В переписи файлы, где обращений к закрытиям больше нет: " + string.Join(", ", stale));
    }

    /// <summary>
    /// Ключи совещательных замков различны, а ключ записи в учёт у ядра и у контрактов модулей —
    /// один. Разойдись две константы, запись и закрытие перестали бы видеть друг друга, и не упало
    /// бы ничего; совпади ключ с замком прогона тестов — закрытие в тесте ждало бы весь прогон.
    /// </summary>
    [Fact]
    public void Ключи_замков_сверены()
    {
        Assert.Equal(AdvisoryLockKeys.PeriodWrite, OpenPeriodWrite.LockKey);
        Assert.NotEqual(TestRunDatabase.RunLock, AdvisoryLockKeys.PeriodWrite);
    }

    private static IEnumerable<string> SourceFiles(string project) =>
        Directory.EnumerateFiles(Path.Combine(SolutionDir, project), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"));

    private static string Relative(string full) =>
        Path.GetRelativePath(SolutionDir, full).Replace('\\', '/');

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
