using System.Text.RegularExpressions;
using BHS.CRG.Tests.Common;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Запись мимо отслеживания по журналу отчёта (ТЗ WORK-12, CORE-3; change <c>work-14-report-journal</c>):
/// <c>ExecuteUpdate</c>, <c>ExecuteDelete</c> и сырой SQL (<c>ExecuteSql*</c>, <c>FromSql*</c>, <c>SqlQuery*</c>) в
/// исходниках модуля рядом с журналом запрещены.
///
/// <para><b>От чего это.</b> Пометка <c>IAppendOnly</c> отвергает правку и удаление записи на сохранении контекста — а
/// массовая операция и голый SQL идут мимо отслеживания, то есть мимо этой проверки (<c>IAppendOnly.cs</c>). Такой
/// запрос компилируется, работает и переписывает свидетельство. Поймать его можно только по тексту — приём
/// <see cref="InvoiceWritePathTests" />.</para>
///
/// <para><b>Как.</b> Файл, где журнал упомянут (тип записи или имя таблицы), не содержит ни одной операции мимо
/// отслеживания. Строже, чем «по журналу», нарочно: связать операцию с набором по тексту надёжно нельзя, а операция
/// по соседней таблице в файле журнала — повод вынести её в другой файл.</para>
/// </summary>
public class WorkJournalWritePathTests
{
    private const string Module = "BHS.CRG.Modules.Work";

    private static readonly Regex Mentions = new(@"\bReportJournalEntry\b|\breport_journal\b", RegexOptions.Compiled);

    private static readonly Regex Bulk = new(
        @"\.(ExecuteUpdate|ExecuteDelete|ExecuteSql\w*|FromSql\w*|SqlQuery\w*)(Async)?\s*[<(]", RegexOptions.Compiled);

    /// <summary>Нарушения в одном файле: операция мимо отслеживания в файле, где упомянут журнал.</summary>
    internal static IEnumerable<string> Violations(string rel, string text)
    {
        var code = SourceTree.WithoutComments(text);
        if (!Mentions.IsMatch(code)) yield break;
        foreach (Match m in Bulk.Matches(code))
            yield return $"{rel}:{SourceTree.LineOf(code, m.Index)}  {m.Groups[1].Value}{m.Groups[2].Value}";
    }

    [Fact]
    public void Журнал_не_пишут_мимо_отслеживания_контекста()
    {
        var bulk = SourceTree.Files(Module)
            .SelectMany(f => Violations(SourceTree.Relative(f), File.ReadAllText(f)))
            .ToList();

        Assert.True(bulk.Count == 0,
            "Рядом с журналом отчёта — запись мимо отслеживания контекста:\n" + string.Join("\n", bulk) + "\n\n" +
            "Журнал только дописывается (ТЗ WORK-12): правку и удаление записи отвергает контекст, а ExecuteUpdate, " +
            "ExecuteDelete и голый SQL идут мимо этой проверки и переписали бы свидетельство. Пишите журнал через " +
            "ReportJournal.Append.");
    }

    /// <summary>Сторож срабатывает: <c>ExecuteDelete</c> по журналу, вставленный в модуль, называет файл и строку.</summary>
    [Fact]
    public void Вставка_ExecuteDelete_по_журналу_называет_файл()
    {
        const string sneaky = """
            namespace BHS.CRG.Modules.Work.Data;
            public sealed class Sneaky(WorkDbContext db)
            {
                public Task WipeAsync(Guid id, CancellationToken ct) =>
                    db.Set<ReportJournalEntry>().Where(e => e.ReportId == id).ExecuteDeleteAsync(ct);
            }
            """;
        const string raw = """var n = db.Database.ExecuteSqlRawAsync("DELETE FROM work.report_journal");""";
        const string comment = """// db.Set<ReportJournalEntry>().ExecuteDeleteAsync(ct) — так нельзя""";

        Assert.Equal(["BHS.CRG.Modules.Work/Data/Sneaky.cs:5  ExecuteDeleteAsync"],
            Violations("BHS.CRG.Modules.Work/Data/Sneaky.cs", sneaky));
        Assert.Equal(["Raw.cs:1  ExecuteSqlRawAsync"], Violations("Raw.cs", raw));
        Assert.Empty(Violations("Comment.cs", comment));
    }
}
