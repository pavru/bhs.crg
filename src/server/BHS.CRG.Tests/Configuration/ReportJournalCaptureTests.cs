using System.Reflection;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Tests.Common;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Перепись полей, которые журнал отчёта снимает «до и после» (ТЗ WORK-12; change <c>work-14-report-journal</c>):
/// каждое свойство отчёта, строки, материала и участника либо читает <c>ReportJournal.Capture</c>, либо названо здесь с
/// причиной.
///
/// <para><b>От чего это.</b> <c>Capture</c> перечисляет поля руками. Поле, добавленное в отчёт и забытое там, правится
/// как обычно — а журнал пишет о такой правке пустое «что изменилось», и свидетельство теряется без единого красного
/// теста. Сверка — по тексту метода: обращение <c>report.Поле</c>, <c>line.Поле</c>, <c>m.Поле</c>, <c>p.Поле</c>.</para>
/// </summary>
public class ReportJournalCaptureTests
{
    private const string File_ = "BHS.CRG.Modules.Work/Data/ReportJournal.cs";

    /// <summary>Свойства, которых «что изменилось» не несёт, — и почему.</summary>
    private static readonly Dictionary<string, string> NotCaptured = new()
    {
        [$"{nameof(Report)}.{nameof(Report.Id)}"] = "ключ отчёта — колонка report_id записи",
        [$"{nameof(Report)}.{nameof(Report.EmployeeId)}"] =
            "сотрудник правкой не меняется (отказ 400) — колонки исполнителя записи",
        [$"{nameof(Report)}.{nameof(Report.Status)}"] = "смена состояния — запись своего вида (подача, приёмка, возврат)",
        [$"{nameof(Report)}.{nameof(Report.SubmittedAt)}"] = "момент подачи — запись подачи (work-15), а не правка",
        [$"{nameof(Report)}.{nameof(Report.Seq)}"] = "номер последовательности — колонка seq записи",
        [$"{nameof(Report)}.{nameof(Report.CreatedAt)}"] = "служебное время — колонка server_time записи",
        [$"{nameof(Report)}.{nameof(Report.UpdatedAt)}"] = "служебное время — колонка server_time записи",
        [$"{nameof(Report)}.{nameof(Report.EnteredByUserId)}"] = "автор ввода — колонки автора записи",
        [$"{nameof(Report)}.{nameof(Report.EnteredByName)}"] = "автор ввода — колонки автора записи",
        [$"{nameof(ReportLine)}.{nameof(ReportLine.ReportId)}"] = "ссылка на отчёт — колонка report_id записи",
        [$"{nameof(ReportLine)}.{nameof(ReportLine.WorkPlanItemId)}"] =
            "позицию перечня ставит сервер по виду работ, варианту и разделу — они в журнале",
        [$"{nameof(ReportLineMaterial)}.{nameof(ReportLineMaterial.Id)}"] = "ключ — материалы снимаются списком по порядку",
        [$"{nameof(ReportLineMaterial)}.{nameof(ReportLineMaterial.LineId)}"] = "ссылка на строку — список лежит под строкой",
        [$"{nameof(ReportLineParticipant)}.{nameof(ReportLineParticipant.Id)}"] =
            "ключ — участники снимаются списком сотрудников",
        [$"{nameof(ReportLineParticipant)}.{nameof(ReportLineParticipant.LineId)}"] =
            "ссылка на строку — список лежит под строкой",
    };

    private static readonly (Type Type, string Variable)[] Captured =
    [
        (typeof(Report), "report"), (typeof(ReportLine), "line"), (typeof(ReportLineMaterial), "m"),
        (typeof(ReportLineParticipant), "p"),
    ];

    private static string CaptureBody()
    {
        var text = SourceTree.WithoutComments(File.ReadAllText(Path.Combine(SourceTree.SolutionDir, File_)));
        var start = text.IndexOf("ReportSnapshot Capture(", StringComparison.Ordinal);
        var end = text.IndexOf("ReportJournalEntry Append(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"В {File_} не найден метод Capture перед Append — сверять не с чем.");
        return text[start..end];
    }

    private static IEnumerable<(string Key, string Access)> Properties() =>
        Captured.SelectMany(c => c.Type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => ($"{c.Type.Name}.{p.Name}", $"{c.Variable}.{p.Name}")));

    [Fact]
    public void Каждое_поле_отчёта_снимает_журнал_или_названо_с_причиной()
    {
        var body = CaptureBody();
        var missing = Properties()
            .Where(p => !NotCaptured.ContainsKey(p.Key) && !body.Contains(p.Access, StringComparison.Ordinal))
            .Select(p => p.Key)
            .ToList();

        Assert.True(missing.Count == 0,
            "Журнал отчёта не снимает поля:\n" + string.Join("\n", missing) + "\n\n" +
            "Правка такого поля легла бы в журнал пустым «что изменилось». Снимите поле в ReportJournal.Capture — " +
            "или впишите его в NotCaptured с причиной.");
    }

    [Fact]
    public void Перепись_не_хранит_умерших_записей()
    {
        var body = CaptureBody();
        var known = Properties().ToDictionary(p => p.Key, p => p.Access);
        var stale = NotCaptured.Keys
            .Where(k => !known.TryGetValue(k, out var access) || body.Contains(access, StringComparison.Ordinal))
            .ToList();

        Assert.True(stale.Count == 0,
            "В NotCaptured поля, которых нет или которые Capture уже снимает: " + string.Join(", ", stale));
    }

    /// <summary>Сторож срабатывает: свойство вне переписи и вне Capture называется по имени.</summary>
    [Fact]
    public void Сверка_видит_все_четыре_типа()
    {
        var keys = Properties().Select(p => p.Key).ToList();
        foreach (var (type, _) in Captured)
            Assert.Contains(keys, k => k.StartsWith(type.Name + ".", StringComparison.Ordinal));
        Assert.Contains("Report.Hours", keys);
        Assert.Contains("report.Hours", CaptureBody());
    }
}
