using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Modules.Work.Submission;

/// <summary>
/// Автоподача отчётов (ТЗ WORK-10, WORK-41, WORK-6; change <c>work-15-submission</c>) — задача такта модуля
/// (<c>work-08</c>): ядро ставит её раз в <see cref="Period" /> с постоянной целью, и пока одна стоит или идёт, вторая
/// не ставится. Свой фоновый цикл модулю запрещён — он был бы второй очередью.
///
/// <para><b>По состоянию, а не по расписанию.</b> Задача выбирает ВСЕ черновики с истёкшим окном
/// (<c>status = draft and window_ends_at &lt;= now</c>), порциями, — поэтому после простоя одна задача догоняет всё
/// пропущенное. <b>Момент подачи — конец окна, а не время прогона</b>: после трёх дней простоя отчёты поданы своими
/// окнами, а не моментом подъёма сервера.</para>
///
/// <para><b>Возвращённые не трогаются</b>: их окно прошло, а подаёт их снова человек после исправления.</para>
///
/// <para><b>Обязательные поля не ждутся</b> (ТЗ WORK-6, WORK-10): черновик без места подаётся, а незаполненное ложится
/// в <c>requiredMissing</c> и в запись журнала — иначе отчёт остался бы черновиком навсегда, и момент подачи, который
/// ТЗ задаёт временем, сдвинулся бы.</para>
///
/// <para><b>Гонка.</b> Монтажник, правящий отчёт в ту же секунду, выигрывает: конфликт версии строки — пропуск до
/// следующего такта с тем же моментом. Два прогона разом (второй экземпляр) дают одну подачу и одну запись журнала:
/// подача идёт под замком отчётов сотрудника, проигравший получает конфликт версии.</para>
///
/// <para><b>От имени системы</b> (ТЗ WORK-41, CORE-24.1): параметр доступа — система с названием такта
/// (<see cref="IModuleReaders.ForSystem" />), запись журнала отчёта — автор «Система», вид
/// <see cref="ReportJournalKinds.AutoSubmitted" />. Опубликованных наборов задача не читает. В журнал действий ядра —
/// одна запись на прогон с числом поданных, и только если подано что-то: пустой прогон каждые пять минут был бы
/// шумом.</para>
/// </summary>
public sealed class AutoSubmitJob(
    WorkDbContext db, ReportReads reads, ReportSubmission submission, RequiredFields required, IModuleReaders readers,
    IModuleEmployees employees, IModuleActivityLog log, TimeProvider time, ILogger<AutoSubmitJob> logger)
    : IModuleJobHandler
{
    public const string Code = "work.autosubmit";

    public const string Title = "Автоподача отчётов";

    /// <summary>Имя автора записи журнала отчёта (ТЗ WORK-41).</summary>
    public const string SystemAuthor = "Система";

    /// <summary>Как часто ядро ставит задачу: окно кончается в полночь стройки, и пять минут — опоздание подачи.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromMinutes(5);

    /// <summary>Такт модуля — объявление для манифеста (<see cref="WorkModule.Ticks" />).</summary>
    public static readonly ModuleTick Tick = new("autosubmit", Title, Code, Period);

    /// <summary>Сколько черновиков читается за раз.</summary>
    public const int Batch = 100;

    public string Operation => Code;

    public async Task RunAsync(ModuleJobRun run, CancellationToken ct)
    {
        // Автоподача — всегда действие системы, кто бы ни поставил задачу: охват «все» человеку не положен.
        var reader = readers.ForSystem(Title);
        var now = time.GetUtcNow();
        var skipped = new List<Guid>();
        var done = 0;
        while (true)
        {
            var batch = await reads.ExpiredDraftsAsync(reader, now, skipped, Batch, ct);
            if (batch.Count == 0) break;
            foreach (var id in batch)
            {
                if (await SubmitOneAsync(reader, id, now, ct)) done++;
                else skipped.Add(id);
                await run.Report("отчётов", done, done + skipped.Count);
            }
        }

        if (done == 0) return;
        try
        {
            await log.RecordAsync(ReportActions.AutoSubmitted, after: $"подано отчётов: {done}", ct: CancellationToken.None);
        }
        catch (Exception lost)
        {
            // Подачи уже зафиксированы и названы в журналах отчётов — сбой журнала ядра их не отменяет.
            logger.LogWarning(lost, "Автоподача подала {Count} отчётов, а запись в журнал действий не легла.", done);
        }
    }

    /// <summary>Подать один черновик; <c>false</c> — пропущен (его подали или поправили между чтениями).</summary>
    private async Task<bool> SubmitOneAsync(ModuleReader reader, Guid id, DateTimeOffset now, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (await reads.ExpiredDraftAsync(reader, id, now, ct) is not { } report) return false;
            var missing = RequiredFields.Json(await required.OfAsync(report, ct));
            var executor = (await employees.GetAsync([report.EmployeeId], ct))?.FirstOrDefault()?.FullName
                           ?? report.EmployeeId.ToString();
            await submission.ApplyAsync(report, report.WindowEndsAt, ReportSubmitter.Auto, missing,
                new ReportJournalActor(null, SystemAuthor, null), executor, Guid.NewGuid(), deviceTime: null, now, ct);
            await tx.CommitAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogDebug("Автоподача: отчёт {ReportId} изменили между чтениями — подаётся следующим тактом.", id);
            return false;
        }
    }
}
