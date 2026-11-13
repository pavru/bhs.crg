using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Common;

/// <summary>
/// Цикл фоновой службы по расписанию: пауза на старте, затем шаг по таймеру, каждый шаг — под защитой
/// (плановая копия, такт модульных заданий). Один на эти службы, чтобы правка цикла не делалась дважды.
///
/// <para>⚠️ <b>Остановка — только по токену службы.</b> Отмена, пришедшая НЕ от остановки приложения
/// (тайм-аут команды базы приходит <see cref="TaskCanceledException" />), — сбой шага, а не конец
/// цикла: иначе один тайм-аут молча выключал бы расписание до перезапуска сервера (ревью PR #1957).</para>
/// </summary>
public static class PeriodicLoop
{
    /// <param name="what">Чем занят цикл — для строки журнала сервера при сбое шага.</param>
    public static async Task RunAsync(TimeProvider time, TimeSpan startDelay, TimeSpan step,
        Func<CancellationToken, Task> tick, ILogger logger, string what, CancellationToken ct)
    {
        try { await Task.Delay(startDelay, time, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }

        using var timer = new PeriodicTimer(step, time);
        do
        {
            try { await tick(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Сбой цикла: {What}", what); }
        }
        while (await WaitAsync(timer, ct));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
