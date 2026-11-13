using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Jobs;
using BHS.CRG.Infrastructure.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace BHS.CRG.Tests.Jobs;

/// <summary>
/// Цикл служб по расписанию (плановая копия, такт модулей) и правило срока такта (ревью PR #1957).
/// </summary>
public class PeriodicLoopTests
{
    /// <summary>
    /// Отмена, пришедшая НЕ от остановки приложения (тайм-аут команды базы), — сбой шага, а не конец
    /// цикла. Прежний цикл на ней выходил, и расписание молча стояло до перезапуска сервера.
    /// </summary>
    [Fact]
    public async Task Тайм_аут_шага_не_останавливает_цикл()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var calls = 0;

        await PeriodicLoop.RunAsync(TimeProvider.System, TimeSpan.Zero, TimeSpan.FromMilliseconds(10), _ =>
        {
            if (++calls == 1) throw new TaskCanceledException("тайм-аут команды базы");
            if (calls == 3) stop.Cancel();
            return Task.CompletedTask;
        }, NullLogger.Instance, "проба", stop.Token);

        Assert.Equal(3, calls);
    }

    /// <summary>Остановка приложения по-прежнему заканчивает цикл — без исключения наружу.</summary>
    [Fact]
    public async Task Остановка_приложения_заканчивает_цикл()
    {
        using var stop = new CancellationTokenSource();
        var calls = 0;

        await PeriodicLoop.RunAsync(TimeProvider.System, TimeSpan.Zero, TimeSpan.FromMilliseconds(10), ct =>
        {
            calls++;
            stop.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }, NullLogger.Instance, "проба", stop.Token);

        Assert.Equal(1, calls);
    }

    private static JobDto Last(string status, DateTimeOffset created) =>
        new(Guid.NewGuid(), "ModuleWork", Guid.NewGuid(), status, "Такт", null, created, null);

    [Fact]
    public void Срок_такта_считается_от_постановки_и_не_при_идущей_задаче()
    {
        var at = new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);
        var period = TimeSpan.FromMinutes(5);

        Assert.True(ModuleTickService.IsDue(period, null, at));
        Assert.False(ModuleTickService.IsDue(period, Last("Succeeded", at), at.AddMinutes(4)));
        Assert.True(ModuleTickService.IsDue(period, Last("Failed", at), at.AddMinutes(5)));
        Assert.False(ModuleTickService.IsDue(period, Last("Running", at), at.AddDays(7)));
        Assert.False(ModuleTickService.IsDue(period, Last("Queued", at), at.AddDays(7)));
    }

    /// <summary>Нераспознанный статус — отказ шага, а не «задача кончилась»: иначе идущая получила бы вторую.</summary>
    [Fact]
    public void Нераспознанный_статус_последней_задачи_отказ_а_не_повод_ставить()
    {
        var at = DateTimeOffset.UtcNow;
        Assert.Throws<InvalidOperationException>(
            () => ModuleTickService.IsDue(TimeSpan.FromMinutes(5), Last("выполняется", at), at.AddDays(1)));
    }
}
