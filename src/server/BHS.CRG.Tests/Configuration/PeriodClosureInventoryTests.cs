using System.Text.RegularExpressions;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Data;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Common;

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
    private static readonly string[] Projects =
        SolutionModules.WithCore("BHS.CRG.Api", "BHS.CRG.Application", "BHS.CRG.Infrastructure");

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

        foreach (var file in Projects.SelectMany(SourceTree.Files))
        {
            var rel = SourceTree.Relative(file);
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
                var path = Path.Combine(SourceTree.SolutionDir, rel.Replace('/', Path.DirectorySeparatorChar));
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

        // ВСЕ ключи реестра, а не одна пара: следующий замок, добавленный в реестр с уже занятым
        // числом, обязан упасть здесь, а не ждать чужой замок в бою (ревью PR #1189).
        var keys = typeof(AdvisoryLockKeys)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (f.Name, Key: Convert.ToInt64(f.GetRawConstantValue())))
            .Append((Name: "TestRunDatabase.RunLock", Key: TestRunDatabase.RunLock))
            .ToList();

        Assert.True(keys.Count >= 2);
        var clashes = keys.GroupBy(k => k.Key).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(k => k.Name))}").ToList();
        Assert.True(clashes.Count == 0, "Один ключ у двух замков: " + string.Join("; ", clashes));
    }
}
