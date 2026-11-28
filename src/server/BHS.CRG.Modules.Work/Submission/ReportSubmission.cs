using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Crew;

namespace BHS.CRG.Modules.Work.Submission;

/// <summary>
/// Шаг подачи отчёта (ТЗ WORK-9, WORK-10, WORK-41; change <c>work-15-submission</c>) — один на ручную подачу
/// (<c>ReportDesk</c>) и автоподачу (<see cref="AutoSubmitJob" />): статус и момент, номер последовательности, запись
/// протокола и запись журнала отчёта — одним ходом в транзакции вызывающего. Нет записи журнала — нет и подачи
/// (ТЗ WORK-51).
///
/// <para><b>Номер — под замком отчётов сотрудника</b> (<see cref="WorkDbContext.NextReportSeqAsync" />): правка,
/// ручная подача и автоподача одного сотрудника идут по очереди. Проигравший гонку получает конфликт версии строки при
/// сохранении — его ловит вызывающий: ручная подача отвечает «уже подан», автоподача пропускает отчёт до следующего
/// такта.</para>
/// </summary>
public sealed class ReportSubmission(WorkDbContext db, ReportJournal journal, CrewInputs crew)
{
    /// <summary>Подать отчёт, прочитанный с отслеживанием и со строками. Транзакцию открывает и фиксирует вызывающий.</summary>
    /// <param name="at">Момент подачи: у ручной — по правилу окна, у автоподачи — конец окна.</param>
    /// <param name="missing">Незаполненное к автоподаче — колонкой <c>required_missing</c>; у ручной <c>null</c>.</param>
    public async Task ApplyAsync(
        Report report, DateTimeOffset at, ReportSubmitter by, string? missing, ReportJournalActor actor, string executor,
        Guid changeId, DateTimeOffset? deviceTime, DateTimeOffset now, CancellationToken ct)
    {
        var before = ReportJournal.Capture(report);
        if (report.Lines.Any(l => l.IsCrew)) await crew.LockAsync(report, ct);
        var seq = await db.NextReportSeqAsync(report.EmployeeId, ct);
        report.Submit(at, by, missing, seq, now);
        await crew.SubmissionChangedAsync(report, ct);
        await db.SaveChangesAsync(ct);
        db.Add(ReportChange.Of(seq, report, changeId, ReportChangeKind.Submitted, deviceTime, now, db.VersionOf(report)));
        var kind = by == ReportSubmitter.Auto ? ReportJournalKinds.AutoSubmitted : ReportJournalKinds.Submitted;
        journal.Append(kind, report, before, actor, executor, changeId, seq, deviceTime, now);
        await db.SaveChangesAsync(ct);
    }

    internal async Task RecordFieldReceiptAsync(Report report, ReportJournalActor actor, string executor,
        Guid changeId, DateTimeOffset deviceTime, DateTimeOffset now, CancellationToken ct)
    {
        var before = ReportJournal.Capture(report);
        var seq = await db.NextReportSeqAsync(report.EmployeeId, ct);
        report.Touch(seq, now);
        await db.SaveChangesAsync(ct);
        db.Add(ReportChange.Of(seq, report, changeId, ReportChangeKind.FieldReceipt, deviceTime, now, db.VersionOf(report)));
        journal.Append(ReportJournalKinds.FieldReceipt, report, before, actor, executor, changeId, seq, deviceTime, now);
        await db.SaveChangesAsync(ct);
    }
}
