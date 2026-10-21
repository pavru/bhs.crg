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
/// <para><b>Сутки и время — пояса компании</b> (issue #1468), а не часов сервера: сервер в поставке
/// живёт в UTC, и «03:00» по его часам приходилось бы пересчитывать в уме. Пояс читается на каждом
/// тике, поэтому смена пояса компании сдвигает ближайший запуск со следующей минуты.</para>
///
/// <para><b>Свою работу служба не делает сама:</b> она ставит ту же задачу
/// <see cref="JobKind.CreateBackup" />, что и кнопка в интерфейсе. Один путь снятия копии на
/// систему — значит, плановая копия не может однажды разойтись с ручной ни составом, ни поведением
/// при отказе.</para>
/// </summary>
public class BackupScheduleService(
    IServiceScopeFactory scopeFactory,
    ILogger<BackupScheduleService> logger,
    TimeProvider time) : BackgroundService
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
            try { await TickAsync(ct); }
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

    /// <summary>
    /// Одна проверка срока. Открыта ради теста, что служба берёт пояс КОМПАНИИ: правило <see
    /// cref="IsDue" /> проверено отдельно, но без этого теста служба могла бы звать его с поясом
    /// сервера — и все проверки правила остались бы зелёными.
    /// </summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        var schedule = (await sp.GetRequiredService<IIntegrationSettings>().GetEffectiveAsync(ct)).Backup;
        if (!schedule.Enabled) return;

        // Пояс — на каждом тике и не из снимка интеграций: тот кэшируется, а смена пояса
        // (PUT /api/settings/company) его не сбрасывает. Служба — синглтон, и пояс, снятый однажды,
        // пережил бы смену до перезапуска.
        var zone = await sp.GetRequiredService<IAppSettingsStore>().GetCompanyTimeZoneAsync(ct);

        var stateStore = sp.GetRequiredService<ServiceStateStore>();
        var state = await stateStore.LoadAsync<BackupScheduleState>(BackupScheduleStateKeys.Schedule, ct);
        if (!IsDue(schedule, state.LastRunAt, now, zone)) return;

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
    /// и «за неделю простоя копия одна, а не семь». «Сегодня» и срок — в поясе <paramref name="zone" />,
    /// сравнение — моментами: смещение у <paramref name="now" /> значения не имеет.
    ///
    /// Следствие правила, принятое сознательно: перенос пояса на запад после сегодняшней копии даёт
    /// ещё одну — по новому поясу срок прошёл, а копии после него не было. Лишняя копия, а не
    /// потерянная; пропуска суток не бывает ни в какую сторону.
    /// </summary>
    public static bool IsDue(BackupScheduleSettings schedule, DateTimeOffset? lastRunAt,
        DateTimeOffset now, TimeZoneInfo zone)
    {
        if (!schedule.Enabled) return false;
        if (ParseTimeOfDay(schedule.TimeOfDay) is not { } time) return false;

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var dueToday = DueOn(today, time, zone);
        if (now < dueToday) return false;

        // Копию не ставили никогда — свежая установка. Ждать до завтрашней ночи не будем: система,
        // проработавшая день без единой копии, — ровно то состояние, ради которого всё это заведено.
        return lastRunAt is not { } last || last < dueToday;
    }

    /// <summary>
    /// Когда служба поставит следующую плановую копию (issue #1468) — для строки «Ближайшая копия» у
    /// формы расписания. Тем же правилом, что <see cref="IsDue" />, и в том же поясе: строка, считанная
    /// отдельно, разошлась бы с тем, что служба сделает на самом деле.
    ///
    /// Срок настал, а копию не ставили — <see cref="BackupNextRun.Due" />: служба поставит её на
    /// ближайшем тике. Признаком, а не моментом «сейчас»: сравнивать момент с часами браузера клиент
    /// не может — их расхождение с сервером превращало «сейчас» в «сегодня, 09:30» (ревью PR #1561).
    /// По той же причине «сегодня» компании считает сервер. <c>null</c> — расписание выключено или
    /// время негодное: копии не будет.
    /// </summary>
    public static BackupNextRun? NextRunAt(BackupScheduleSettings schedule, DateTimeOffset? lastRunAt,
        DateTimeOffset now, TimeZoneInfo zone)
    {
        if (!schedule.Enabled) return null;
        if (ParseTimeOfDay(schedule.TimeOfDay) is not { } time) return null;

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var dueToday = DueOn(today, time, zone);
        if (now < dueToday) return new BackupNextRun(dueToday, false, today);
        // Срок сегодняшних суток прошёл: копии после него не было — она сейчас, была — завтра.
        return lastRunAt is { } last && last >= dueToday
            ? new BackupNextRun(DueOn(today.AddDays(1), time, zone), false, today)
            : new BackupNextRun(dueToday, true, today);
    }

    /// <summary>
    /// Срок суток <paramref name="day" /> в поясе <paramref name="zone" /> — момент. Смещение берётся
    /// у СРОКА, а не у «сейчас»: иначе в день перевода часов срок, назначенный до перевода, после него
    /// сдвигался на час, и копия снималась второй раз.
    ///
    /// Весной часы переводят вперёд, и времени из разрыва в эти сутки нет — копия в первую минуту
    /// после него. Шаг — минута, а не час: разрывы бывают и по 30 минут. Осенью время бывает дважды —
    /// копия в первый раз (большее смещение — более ранний момент); второй раз она не снимется:
    /// срок у суток один.
    /// </summary>
    public static DateTimeOffset DueOn(DateOnly day, TimeSpan time, TimeZoneInfo zone)
    {
        // Kind = Unspecified обязательно: с Kind.Local TimeZoneInfo считал бы от пояса сервера.
        var local = day.ToDateTime(TimeOnly.FromTimeSpan(time), DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    /// <summary>«ЧЧ:ММ» → время суток; <c>null</c> — запись негодная.</summary>
    public static TimeSpan? ParseTimeOfDay(string? value)
        => TimeSpan.TryParseExact(value?.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var t)
           && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1)
            ? t
            : null;
}

/// <summary>
/// Ближайшая плановая копия (issue #1468): срок со смещением пояса компании — клиент читает день и час
/// из записи как есть; <paramref name="Due" /> — срок прошёл, копия будет на ближайшем тике;
/// <paramref name="Today" /> — сегодня у компании, чтобы клиент сказал «сегодня» или «завтра».
/// </summary>
public sealed record BackupNextRun(DateTimeOffset At, bool Due, DateOnly Today);
