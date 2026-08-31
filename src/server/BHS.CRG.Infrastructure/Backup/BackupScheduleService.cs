using System.Globalization;
using BHS.CRG.Application.Jobs;
using BHS.CRG.Application.Settings;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Jobs;
using BHS.CRG.Infrastructure.Updates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Backup;

/// <summary>
/// Ставит плановую резервную копию раз в сутки (issue #832).
///
/// <para><b>Почему включено по умолчанию.</b> Копия, которую забыли настроить, — самый частый
/// способ потерять данные. Ручная копия защищает только тех, кто о ней помнит; установка, где
/// администратор не сделал ничего, обязана быть защищена всё равно.</para>
///
/// <para><b>Пропущенный запуск.</b> Сервер, выключенный на ночь, к трём часам не существует.
/// Служба поэтому проверяет не «настал ли ровно этот момент», а «прошёл ли сегодняшний срок и не
/// ставили ли мы копию после него»: включённый утром сервер снимет копию утром. Догонять несколько
/// пропущенных суток она не будет — за неделю простоя нужна одна копия, а не семь.</para>
///
/// <para><b>Свою работу служба не делает сама:</b> она ставит ту же задачу
/// <see cref="JobKind.CreateBackup" />, что и кнопка в интерфейсе. Один путь снятия копии на
/// систему — значит, плановая копия не может однажды разойтись с ручной ни составом, ни поведением
/// при отказе.</para>
/// </summary>
public class BackupScheduleService(
    IServiceScopeFactory scopeFactory,
    ILogger<BackupScheduleService> logger) : BackgroundService
{
    /// <summary>
    /// Как часто смотреть на часы. Минута — потому что срок задан с точностью до минуты, а стоит
    /// проверка чтения настроек из кэша и одного сравнения дат: в базу служба идёт только когда
    /// срок настал.
    /// </summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Пауза перед первой проверкой. При запуске системе есть чем заняться (миграции, прогрев), а
    /// пропущенная за ночь копия подождёт ещё две минуты.
    /// </summary>
    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(StartDelay, ct); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Tick);
        do
        {
            try { await TickAsync(DateTimeOffset.Now, ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Сбой цикла планового копирования"); }
        }
        while (await SafeWait(timer, ct));
    }

    private static async Task<bool> SafeWait(PeriodicTimer t, CancellationToken ct)
    {
        try { return await t.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    private async Task TickAsync(DateTimeOffset now, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        var schedule = (await sp.GetRequiredService<IIntegrationSettings>().GetEffectiveAsync(ct)).Backup;
        if (!schedule.Enabled) return;

        var stateStore = sp.GetRequiredService<ServiceStateStore>();
        var state = await stateStore.LoadAsync<BackupScheduleState>(BackupScheduleStateKeys.Schedule, ct);
        if (!IsDue(schedule, state.LastRunAt, now)) return;

        var jobs = sp.GetRequiredService<IJobService>();
        // Копия уже снимается — вручную или прошлой плановой, застрявшей дольше суток. Второй такой
        // же задачей делу не поможешь: она встанет в очередь за первой и прочитает базу дважды.
        if (await jobs.HasActiveOfKindAsync(JobKind.CreateBackup, ct))
        {
            logger.LogInformation("Плановое копирование пропущено: копия уже снимается");
            return;
        }

        // Срок отмечаем ДО постановки задачи. Иначе отказ на постановке (база моргнула) означал бы
        // повтор через минуту — и так до утра, с уведомлением на каждую попытку.
        state.LastRunAt = now;
        await stateStore.SaveAsync(BackupScheduleStateKeys.Schedule, state, ct);

        // Владельца у плановой задачи нет: её поставил не человек. Отказ такой задачи уходит
        // общесистемным уведомлением (см. JobBackgroundService), а не в личный колокольчик.
        try
        {
            await jobs.EnqueueAsync(JobKind.CreateBackup, Guid.Empty, Guid.Empty,
                "Плановое резервное копирование", "{\"scheduled\":true}", ct);
        }
        catch (ConflictException)
        {
            // Копию успели начать вручную между проверкой выше и этой строкой: с issue #900 такое
            // столкновение отвергает база. Это не сбой, а тот же случай, что и проверка «уже
            // снимается», — и говорить о нём надо так же. Молчаливый проброс был бы хуже всего:
            // срок уже отмечен, цикл принял бы отказ за общий сбой, и ночь осталась бы без копии,
            // причём в журнале не было бы сказано почему.
            logger.LogInformation("Плановое копирование пропущено: копия уже снимается");
            return;
        }
        logger.LogInformation("Плановое резервное копирование поставлено в очередь");
    }

    /// <summary>
    /// Настал ли срок. Отдельно и без зависимостей — это единственное правило службы, и проверяется
    /// оно тестами, а не наблюдением за ночным сервером.
    ///
    /// Правило: сегодняшний срок уже прошёл, а последняя постановка была раньше него. Отсюда само
    /// собой следует и «одна копия в сутки», и «пропущенное за ночь снимается при первом запуске»,
    /// и «за неделю простоя копия одна, а не семь».
    /// </summary>
    public static bool IsDue(BackupScheduleSettings schedule, DateTimeOffset? lastRunAt, DateTimeOffset now)
    {
        if (!schedule.Enabled) return false;
        if (ParseTimeOfDay(schedule.TimeOfDay) is not { } time) return false;

        var dueToday = new DateTimeOffset(now.Year, now.Month, now.Day,
            time.Hours, time.Minutes, 0, now.Offset);
        if (now < dueToday) return false;

        // Копию не ставили никогда — свежая установка. Ждать до завтрашней ночи не будем: система,
        // проработавшая день без единой копии, — ровно то состояние, ради которого всё это заведено.
        return lastRunAt is not { } last || last < dueToday;
    }

    /// <summary>«ЧЧ:ММ» → время суток; <c>null</c> — запись негодная.</summary>
    public static TimeSpan? ParseTimeOfDay(string? value)
        => TimeSpan.TryParseExact(value?.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var t)
           && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1)
            ? t
            : null;
}
