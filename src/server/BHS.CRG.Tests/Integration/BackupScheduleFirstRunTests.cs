using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Jobs;
using BHS.CRG.Application.Settings;
using BHS.CRG.Domain.Jobs;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Infrastructure.Jobs;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Updates;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Установка без истории запусков — начало отсчёта, а не пропущенный срок (issue #1403, [CORE-25]).
///
/// Правило проверено отдельно (<see cref="BackupScheduleService.IsDue" />, при пустом отсчёте — от
/// «сейчас»). Здесь — то, чего правило не видит: что служба ЗАПИСЫВАЕТ начало отсчёта (иначе «сейчас»
/// сдвигалось бы с каждым тиком, и копия не встала бы никогда) и что строка «Ближайшая копия» считает
/// от него же, а не от одного лишь «последнего запуска».
/// </summary>
[Collection("Integration")]
public class BackupScheduleFirstRunTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private const string Password = "Passw0rd!";
    private static readonly TimeSpan Msk = TimeSpan.FromHours(3);
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static DateTimeOffset At(int day, int hour, int minute = 0)
        => new(2026, 8, day, hour, minute, 0, Msk);

    /// <summary>
    /// Хост БЕЗ исполнителя фоновых задач — как в <see cref="BackupScheduleZoneTests" />: поставленная
    /// копия иначе снялась бы по-настоящему. Проверяется постановка, а не выполнение.
    /// </summary>
    private WebApplicationFactory<Program> HostWithoutRunner()
        => fixture.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<JobBackgroundService>();
            foreach (var d in services
                         .Where(d => d.ImplementationType == typeof(JobBackgroundService)).ToList())
                services.Remove(d);
        }));

    private static async Task ConfigureAsync(IServiceProvider services, bool enabled, string time,
        BackupScheduleState? state = null)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAppSettingsStore>()
            .SetAsync(AppSettingKeys.CompanyTimeZone, "Europe/Moscow");
        await scope.ServiceProvider.GetRequiredService<IIntegrationSettings>()
            .SaveBackupScheduleAsync(new BackupScheduleSettings { Enabled = enabled, TimeOfDay = time, KeepCount = 7 });
        if (state is not null)
            await scope.ServiceProvider.GetRequiredService<ServiceStateStore>()
                .SaveAsync(BackupScheduleStateKeys.Schedule, state, CancellationToken.None);
    }

    private static async Task<BackupScheduleState> StateAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ServiceStateStore>()
            .LoadAsync<BackupScheduleState>(BackupScheduleStateKeys.Schedule, CancellationToken.None);
    }

    private static async Task<bool> ActiveBackupAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IJobService>()
            .HasActiveOfKindAsync(JobKind.CreateBackup, CancellationToken.None);
    }

    /// <summary>
    /// Свежая установка стартует в 10:00 при расписании на 03:00: первый тик копию не ставит, а
    /// записывает начало отсчёта; «последний запуск» остаётся пустым — копии не было. Завтра в 03:00 —
    /// копия.
    /// </summary>
    [Fact]
    public async Task EmptyHistory_FirstTickStartsCounting_CopyAtNextDue()
    {
        await using var host = HostWithoutRunner();
        await ConfigureAsync(host.Services, enabled: true, time: "03:00");

        var clock = new Clock(At(20, 10, 0));
        var service = new BackupScheduleService(
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<BackupScheduleService>.Instance, clock);

        await service.TickAsync(CancellationToken.None);
        Assert.False(await ActiveBackupAsync(host.Services));
        var started = await StateAsync(host.Services);
        Assert.Equal(At(20, 10, 0), started.CountingSince);
        Assert.Null(started.LastRunAt);

        // Тики до срока не сдвигают начало отсчёта и копию не ставят.
        clock.Now = At(21, 2, 59);
        await service.TickAsync(CancellationToken.None);
        Assert.False(await ActiveBackupAsync(host.Services));
        Assert.Equal(At(20, 10, 0), (await StateAsync(host.Services)).CountingSince);

        clock.Now = At(21, 3, 0);
        await service.TickAsync(CancellationToken.None);
        Assert.True(await ActiveBackupAsync(host.Services));
        Assert.Equal(At(21, 3, 0), (await StateAsync(host.Services)).LastRunAt);
    }

    /// <summary>
    /// Расписание выключено — начало отсчёта не пишется: включат позже, и отсчёт начнётся тогда, а не
    /// с давнего момента, от которого срок оказался бы пропущенным (D3).
    /// </summary>
    [Fact]
    public async Task Disabled_DoesNotStartCounting()
    {
        await using var host = HostWithoutRunner();
        await ConfigureAsync(host.Services, enabled: false, time: "03:00");

        var service = new BackupScheduleService(
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<BackupScheduleService>.Instance, new Clock(At(20, 10, 0)));
        await service.TickAsync(CancellationToken.None);

        Assert.Null((await StateAsync(host.Services)).CountingSince);
        Assert.False(await ActiveBackupAsync(host.Services));
    }

    /// <summary>
    /// Строка «Ближайшая копия» на пустой истории после срока — завтра, не «сейчас». Срок 00:00 по
    /// Москве (без перевода часов) прошёл в любой момент суток, поэтому часы — настоящие: подставные
    /// разошлись бы с часами, по которым выдаётся токен входа.
    /// </summary>
    [Fact]
    public async Task Files_EmptyHistory_NextRunTomorrow()
    {
        await ConfigureAsync(fixture.Services, enabled: true, time: "00:00");
        var schedule = await ScheduleAsync();

        Assert.Equal(JsonValueKind.Null, schedule.GetProperty("lastRunAt").ValueKind);
        var next = schedule.GetProperty("nextRun");
        Assert.False(next.GetProperty("due").GetBoolean());
        var today = DateOnly.Parse(next.GetProperty("today").GetString()!);
        Assert.Equal(BackupScheduleService.DueOn(today.AddDays(1), TimeSpan.Zero, Moscow),
            next.GetProperty("at").GetDateTimeOffset());
    }

    /// <summary>
    /// Начало отсчёта записано до сегодняшнего срока (вчера), копии не было — строка говорит «сейчас»,
    /// как и служба на ближайшем тике. Без передачи начала отсчёта строка отвечала бы «завтра» и
    /// разошлась бы со службой.
    /// </summary>
    [Fact]
    public async Task Files_CountingSinceBeforeTodaysDue_NextRunIsDue()
    {
        await ConfigureAsync(fixture.Services, enabled: true, time: "00:00",
            new BackupScheduleState { CountingSince = DateTimeOffset.UtcNow.AddDays(-2) });
        var schedule = await ScheduleAsync();

        Assert.Equal(JsonValueKind.Null, schedule.GetProperty("lastRunAt").ValueKind);
        Assert.True(schedule.GetProperty("nextRun").GetProperty("due").GetBoolean());
    }

    private async Task<JsonElement> ScheduleAsync()
    {
        var admin = await SignInAdminAsync();
        var response = await admin.GetAsync("/api/backup/files");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("schedule");
    }

    private async Task<HttpClient> SignInAdminAsync()
    {
        var email = $"admin_{Guid.NewGuid():N}@test.local";
        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                UserName = email, Email = email, DisplayName = "Тест", EmailConfirmed = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, SystemRoles.Admin)).Succeeded);
        }

        var client = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
