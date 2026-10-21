using BHS.CRG.Application.Jobs;
using BHS.CRG.Application.Settings;
using BHS.CRG.Domain.Jobs;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Infrastructure.Jobs;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Плановая копия ставится по поясу КОМПАНИИ, а не сервера (issue #1468) — проверка самой службы.
///
/// Правило <see cref="BackupScheduleService.IsDue" /> проверено отдельно, с поясом параметром. Но
/// служба могла бы звать его с поясом сервера, и все те проверки остались бы зелёными. Здесь служба
/// живая: пояс компании записан в настройки, часы подставные, а пояс выбран заведомо НЕ равным поясу
/// машины — на стенде разработчика это Москва, в CI — UTC.
/// </summary>
[Collection("Integration")]
public class BackupScheduleZoneTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>
    /// Хост БЕЗ исполнителя фоновых задач: поставленная копия иначе снялась бы по-настоящему —
    /// с чтением тестовой базы посреди чужих тестов. Проверяется постановка, а не выполнение.
    /// </summary>
    private WebApplicationFactory<Program> HostWithoutRunner()
        => fixture.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<JobBackgroundService>();
            foreach (var d in services
                         .Where(d => d.ImplementationType == typeof(JobBackgroundService)).ToList())
                services.Remove(d);
        }));

    [Fact]
    public async Task Tick_UsesCompanyZone_NotServerZone()
    {
        // Пояс компании — не пояс машины: иначе тест прошёл бы и у службы, читающей пояс сервера.
        var zoneId = TimeZoneInfo.Local.BaseUtcOffset == TimeSpan.FromHours(10) ? "Europe/Moscow" : "Asia/Vladivostok";
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var due = BackupScheduleService.DueOn(new DateOnly(2026, 8, 20), new TimeSpan(3, 0, 0), zone);

        await using var host = HostWithoutRunner();
        using (var scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAppSettingsStore>()
                .SetAsync(AppSettingKeys.CompanyTimeZone, zoneId);
            await scope.ServiceProvider.GetRequiredService<IIntegrationSettings>()
                .SaveBackupScheduleAsync(new BackupScheduleSettings { Enabled = true, TimeOfDay = "03:00", KeepCount = 7 });
        }

        var clock = new Clock(due.AddMinutes(-1));
        var service = new BackupScheduleService(
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<BackupScheduleService>.Instance, clock);

        // Минута до срока по поясу компании. По поясу машины 03:00 этих суток давно прошло — служба
        // с поясом сервера поставила бы копию уже здесь.
        await service.TickAsync(CancellationToken.None);
        Assert.False(await ActiveBackupAsync(host));

        clock.Now = due;
        await service.TickAsync(CancellationToken.None);
        Assert.True(await ActiveBackupAsync(host));
    }

    private static async Task<bool> ActiveBackupAsync(WebApplicationFactory<Program> host)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IJobService>()
            .HasActiveOfKindAsync(JobKind.CreateBackup, CancellationToken.None);
    }
}
