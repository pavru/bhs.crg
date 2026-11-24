using System.Text.RegularExpressions;
using BHS.CRG.Tests.Common;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Перепись обращений к журналу отчёта монтажника (ТЗ WORK-12, WORK-51, CORE-3; change <c>work-14-report-journal</c>):
/// журнал пишет одно место — <c>ReportJournal</c>, читает один файл чтения — <c>ReportReads</c>.
///
/// <para><b>От чего это.</b> Запись журнала обязана две вещи, которых адрес по памяти не сделает: лечь одним
/// сохранением с протоколом и правкой (нет записи — нет правки) и снять «что изменилось» сравнением состояний до и после.
/// Запись, добавленная мимо, работает — и молча оставляет правку без свидетельства или свидетельство без правки. Правку
/// и удаление записей отвергает контекст (<c>IAppendOnly</c>), запись мимо отслеживания — сторож
/// <see cref="WorkJournalWritePathTests" />; эта перепись закрывает третий путь — второе место записи. Приём
/// <see cref="InvoiceBookInventoryTests" />.</para>
/// </summary>
public class WorkJournalInventoryTests
{
    private const string Module = "BHS.CRG.Modules.Work";

    /// <summary>Кому позволено обращаться к журналу — и почему.</summary>
    private static readonly Dictionary<string, string> MayTouchTheJournal = new()
    {
        [$"{Module}/Data/ReportJournal.cs"] =
            "единственное место записи: сравнение до и после, автор и исполнитель снимком, правка после подачи",
        [$"{Module}/Data/WorkReportModel.cs"] = "отображение записи на таблицу: запросов нет",
        [$"{Module}/WorkReferences.cs"] = "объявление колонок с идентификаторами ядра: запросов нет",
        [$"{Module}/WorkModule.cs"] = "объявление запомненных путей к файлам для резервной копии: запросов нет",
        [$"{Module}/Data/ReportReads.cs"] =
            "файл чтения отчёта (IIsolatedRecord): журнал читается только с охватом читателя, без отслеживания",
    };

    // Набор достаётся и мимо имени свойства — Set<ReportJournalEntry>() из любого места контекста, — а таблица — сырым
    // запросом; поэтому и тип, и имя таблицы, и фабрика.
    internal static readonly Regex Journal = new(
        @"\bSet\s*<\s*(?:[\w.]+\.)?ReportJournalEntry\s*>|\breport_journal\b|\bReportJournalEntry\.Of\(" +
        @"|\bnew\s+ReportJournalEntry\b",
        RegexOptions.Compiled);

    [Fact]
    public void К_журналу_обращается_только_его_место_записи_и_файл_чтения()
    {
        var outsiders = new List<string>();
        foreach (var file in SourceTree.Files(Module))
        {
            var rel = SourceTree.Relative(file);
            if (MayTouchTheJournal.ContainsKey(rel)) continue;

            var text = SourceTree.WithoutComments(File.ReadAllText(file));
            foreach (Match m in Journal.Matches(text))
                outsiders.Add($"{rel}:{SourceTree.LineOf(text, m.Index)}");
        }

        Assert.True(outsiders.Count == 0,
            "К журналу отчёта обратились мимо ReportJournal:\n" + string.Join("\n", outsiders) + "\n\n" +
            "Журнал пишет только ReportJournal.Append — одним сохранением с протоколом и правкой и со сравнением " +
            "состояний до и после. Если обращение нужно (чтение для экрана) — впишите файл в MayTouchTheJournal с причиной.");
    }

    [Fact]
    public void Перепись_не_хранит_умерших_записей()
    {
        var stale = MayTouchTheJournal.Keys
            .Where(rel => !File.Exists(Path.Combine(SourceTree.SolutionDir, rel))
                          || !Journal.IsMatch(SourceTree.WithoutComments(File.ReadAllText(Path.Combine(SourceTree.SolutionDir, rel)))))
            .ToList();

        Assert.True(stale.Count == 0, "В переписи журнала файлы, где обращения больше нет: " + string.Join(", ", stale));
    }
}
