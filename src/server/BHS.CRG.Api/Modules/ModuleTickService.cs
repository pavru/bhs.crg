using BHS.CRG.Application.Jobs;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Jobs;
using BHS.CRG.Infrastructure.Common;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Modules;

/// <summary>
/// Периодический такт модульных заданий (ТЗ WORK-41, WORK-10; work-08): раз в минуту смотрит на такты
/// включённых модулей и ставит задачу такта, если срок прошёл и после него задачу не ставили.
///
/// <para><b>По образцу плановой копии</b> (<c>BackupScheduleService</c>): правило «прошёл ли срок и не
/// ставили ли после него», а не «настал ли ровно этот момент». Поэтому после простоя сервера такт
/// ставит ОДНУ задачу, а не задачу за каждый пропущенный срок; догон — свойство запроса модуля
/// («все черновики, чьё окно истекло»), а не такта.</para>
///
/// <para><b>Свою работу служба не делает.</b> Она ставит задачу операции модуля портом
/// <see cref="IModuleJobs" /> — тем же путём, что и сам модуль, с той же проверкой операции и
/// обработчика, — и задача видна в индикаторе и останавливается вместе с приложением.</para>
///
/// <para><b>Состояния своего нет.</b> «Когда ставили последнюю» и «идёт ли она» читаются из таблицы
/// задач по постоянной цели такта: второе место правды разошлось бы с очередью при первом же сбое между
/// записью состояния и постановкой.</para>
///
/// <para>Живёт в <c>Api</c>, рядом с исполнителем работ модулей: инфраструктура о модулях не знает.</para>
/// </summary>
public sealed class ModuleTickService(
    IServiceScopeFactory scopeFactory,
    ModuleTickCatalog ticks,
    ILogger<ModuleTickService> logger,
    TimeProvider time) : BackgroundService
{
    /// <summary>Шаг службы. Он же наименьший период такта (<see cref="ModuleTick.MinPeriod" />).</summary>
    private static readonly TimeSpan Step = ModuleTick.MinPeriod;

    /// <summary>
    /// Пауза перед первой проверкой — как у плановой копии: при запуске системе есть чем заняться, а
    /// пропущенный за простой срок подождёт ещё две минуты.
    /// </summary>
    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(2);

    protected override Task ExecuteAsync(CancellationToken ct) =>
        // Тактов нет — смотреть не на что: без таймера и без строки в журнале.
        ticks.All.Count == 0
            ? Task.CompletedTask
            : PeriodicLoop.RunAsync(time, StartDelay, Step, TickAsync, logger, "такты модулей", ct);

    /// <summary>
    /// Один шаг: каждый такт — отдельно, и сбой одного не останавливает остальные. Открыт ради тестов:
    /// часы подставляются, а ждать минуту прогону незачем.
    /// </summary>
    public async Task TickAsync(CancellationToken ct)
    {
        foreach (var entry in ticks.All)
        {
            try { await TickOneAsync(entry, ct); }
            // Остановка приложения — конец шага. Иная отмена (тайм-аут команды базы) — сбой ЭТОГО такта:
            // остальные такты шагают дальше, а цикл не кончается (ревью PR #1957).
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Такт «{Tick}» модуля «{Module}» не поставил задачу",
                    entry.Tick.Code, entry.Module);
            }
        }
    }

    private async Task TickOneAsync(ModuleTickEntry entry, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        var last = await sp.GetRequiredService<IJobService>().GetLatestModuleWorkAsync(entry.TargetId, ct);
        if (!IsDue(entry.Tick.Period, last, now)) return;

        try
        {
            await sp.GetRequiredService<IModuleJobs>()
                .EnqueueAsync(entry.Tick.Operation, entry.TargetId, entry.Tick.Title, ct: ct);
        }
        catch (ConflictException)
        {
            // Задачу такта успел поставить другой экземпляр между чтением и постановкой — индекс «одна
            // активная на цель» (issue #900) отверг вторую. Это штатный исход такта, а не сбой: работа
            // уже стоит в очереди. Ошибкой в журнал сервера не пишется.
            logger.LogDebug("Такт «{Tick}» модуля «{Module}»: задача уже выполняется", entry.Tick.Code, entry.Module);
        }
    }

    /// <summary>
    /// Пора ли ставить задачу такта. Отдельно и без зависимостей — единственное правило службы.
    ///
    /// <para>Правило: задачи нет вовсе — пора; прежняя ещё стоит или идёт — нет («уже выполняется»,
    /// штатно); иначе — пора, если с её постановки прошёл период. Отсюда и «одна задача после простоя»:
    /// сравнение с последней постановкой, а не счёт пропущенных сроков.</para>
    ///
    /// <para>⚠️ Отсчёт — от ПОСТАНОВКИ, а не от завершения: такт периодический, и долгая задача не
    /// сдвигает расписание. Упавшая задача тоже отсчитывается от постановки — следующая встанет через
    /// период, а не сразу, и отказ не повторяется каждую минуту уведомлением.</para>
    /// </summary>
    public static bool IsDue(TimeSpan period, JobDto? last, DateTimeOffset now)
    {
        if (last is null) return true;
        // Статус — значением перечисления, а не строкой: нераспознанный статус — отказ шага, а не «задача
        // кончилась» (иначе идущая задача получила бы вторую, ревью PR #1957).
        if (!Enum.TryParse<JobStatus>(last.Status, out var status))
            throw new InvalidOperationException(
                $"Статус задачи такта «{last.Status}» не распознан: идёт ли она, неизвестно, и вторую не ставим.");
        if (status is JobStatus.Queued or JobStatus.Running) return false;
        return now - last.CreatedAt >= period;
    }
}
