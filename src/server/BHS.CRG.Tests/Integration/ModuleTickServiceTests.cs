using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Domain.Jobs;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Tests.Configuration;
using BHS.CRG.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Периодический такт модульных заданий на живом приложении (ТЗ WORK-41, WORK-10; work-08): срок
/// прошёл — одна задача; задача идёт — вторая не ставится и ошибки в журнале сервера нет; неделя
/// простоя — одна задача; модуль выключен — ни одной; задача такта — от имени системы с названием.
///
/// <para>Служба создаётся тестом с подставными часами и шагает по вызову (<see cref="ModuleTickService.TickAsync" />),
/// а очередь, исполнитель, обработчик, журнал и порт параметра доступа — настоящие.</para>
///
/// <para>Часы — одни на хост (<see cref="ModuleTicksHost.Clock" />): по ним такт считает срок, а очередь
/// ставит <c>CreatedAt</c> задачи, как в поставке.</para>
/// </summary>
public class ModuleTickServiceTests(ModuleTicksHost host) : IClassFixture<ModuleTicksHost>, IAsyncLifetime
{
    private ProbeTickGate Gate => host.Services.GetRequiredService<ProbeTickGate>();

    public async Task InitializeAsync()
    {
        Gate.Reset();
        host.Clock.Now = DateTimeOffset.UtcNow;
        await host.ResetDatabaseAsync();
    }

    /// <summary>Задвижку — открыть: задача, оставленная «идущей», не дала бы очистить базу следующему.</summary>
    public Task DisposeAsync()
    {
        Gate.Reset();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Срок_прошёл_ставится_одна_задача_в_общую_очередь()
    {
        var log = new Recording();
        var clock = host.Clock;
        var service = Service(clock, log);

        // Задач такта не было никогда — первая встаёт сразу: ждать периода не от чего.
        await service.TickAsync(default);
        var first = Assert.Single(await JobsAsync());
        Assert.Equal(JobKind.ModuleWork, first.Kind);
        Assert.Equal(ProbeTick.Title, first.Title);
        // Владельца нет: задачу поставил не человек, её отказ — общесистемным уведомлением.
        Assert.Equal(Guid.Empty, first.UserId);
        await Gate.NextAsync();
        await SucceededAsync(first.Id);

        // Две минуты спустя — срок (5 мин) не прошёл.
        clock.Now = first.CreatedAt.AddMinutes(2);
        await service.TickAsync(default);
        Assert.Single(await JobsAsync());

        // Шесть минут спустя, прежняя завершилась — одна новая.
        clock.Now = first.CreatedAt.AddMinutes(6);
        await service.TickAsync(default);
        var jobs = await JobsAsync();
        Assert.Equal(2, jobs.Count);
        await Gate.NextAsync();
        await SucceededAsync(jobs[1].Id);
        Assert.Empty(log.Problems);
    }

    [Fact]
    public async Task Задача_такта_идёт_вторая_не_ставится_и_ошибки_нет()
    {
        var log = new Recording();
        var clock = host.Clock;
        var service = Service(clock, log);

        Gate.Hold();
        await service.TickAsync(default);
        var running = Assert.Single(await JobsAsync());
        await Gate.NextAsync();

        // Срок давно прошёл, а прежняя задача ещё выполняется: «уже выполняется» — штатный исход.
        clock.Now = running.CreatedAt.AddMinutes(30);
        await service.TickAsync(default);
        await service.TickAsync(default);
        Assert.Single(await JobsAsync());
        Assert.Empty(log.Problems);

        // Тот же исход у гонки: вторую задачу на цель такта отвергает индекс «одна активная на цель».
        using (var scope = host.Services.CreateScope())
            await Assert.ThrowsAsync<BHS.CRG.Domain.Common.ConflictException>(() => scope.ServiceProvider
                .GetRequiredService<IModuleJobs>().EnqueueAsync(ProbeTickWork.Code, ProbeTick.Target, ProbeTick.Title));

        Gate.Release();
        await SucceededAsync(running.Id);
    }

    [Fact]
    public async Task После_недели_простоя_одна_задача_а_не_по_одной_за_каждый_срок()
    {
        var log = new Recording();
        var clock = host.Clock;
        var service = Service(clock, log);

        await service.TickAsync(default);
        var before = Assert.Single(await JobsAsync());
        await Gate.NextAsync();
        await SucceededAsync(before.Id);

        // Неделя — две тысячи пропущенных пятиминутных сроков. Такт их не помнит: одна задача, а что
        // накопилось, выбирает её запрос по состоянию.
        Gate.Hold();
        clock.Now = before.CreatedAt.AddDays(7);
        for (var i = 0; i < 3; i++)
        {
            await service.TickAsync(default);
            clock.Now = clock.Now.AddMinutes(1);
        }

        var jobs = await JobsAsync();
        Assert.Equal(2, jobs.Count);
        // Постановка — по тем же часам, что и срок: задача встала «неделю спустя», а не «сейчас машины».
        Assert.Equal(before.CreatedAt.AddDays(7), jobs[1].CreatedAt);
        await Gate.NextAsync();
        Gate.Release();
        await SucceededAsync(jobs[1].Id);

        // Завершилась — а период с её постановки не прошёл: следующей нет.
        await service.TickAsync(default);
        Assert.Equal(2, (await JobsAsync()).Count);
        Assert.Empty(log.Problems);
    }

    /// <summary>
    /// У выключенного модуля такта нет: каталог собирается из ВКЛЮЧЁННЫХ модулей реестра, и тот же
    /// пробный модуль среди выключенных не даёт ни одной задачи.
    /// </summary>
    [Fact]
    public async Task Модуль_выключен_задач_такта_нет()
    {
        var registry = new ModuleRegistry([], [ProbeTick.Module()]);
        var catalog = new ModuleTickCatalog(registry);
        Assert.Empty(catalog.All);

        var service = new ModuleTickService(host.Services.GetRequiredService<IServiceScopeFactory>(), catalog,
            new Recording(), new TestClock(DateTimeOffset.UtcNow.AddDays(7)));
        await service.TickAsync(default);

        Assert.Empty(await JobsAsync());
    }

    /// <summary>
    /// Задача такта идёт от имени системы с названием такта: в журнале — «Система — …» без учётной
    /// записи, параметр доступа — система с той же причиной, и контракт в правах отвечает ей отказом
    /// (ТЗ WORK-41, CORE-24.1).
    /// </summary>
    [Fact]
    public async Task Задача_такта_пишет_журнал_и_читает_данные_от_имени_системы_с_названием()
    {
        await Service(host.Clock, new Recording()).TickAsync(default);
        var job = Assert.Single(await JobsAsync());
        var (run, reader) = await Gate.NextAsync();
        await SucceededAsync(job.Id);

        Assert.Equal(job.Id, run.JobId);
        Assert.Equal(Guid.Empty, run.UserId);

        using var scope = host.Services.CreateScope();
        var record = Assert.Single(await scope.ServiceProvider.GetRequiredService<IActivityLog>()
            .ReadAsync(0, 50, ActivityVisibility.Whole, ProbeModuleActivity.InvoicePaid.Code));
        Assert.Equal("Система — " + ProbeTick.Title, record.ActorName);
        Assert.Null(record.ActorId);

        // Не «автор задачи удалён» — так ответил бы порт задаче без владельца, не будь она тактом.
        Assert.True(reader.IsSystem);
        Assert.Equal(ProbeTick.Title, reader.SystemReason);
        var answer = Assert.IsType<ContractAnswer<int>.SystemRefused>(
            ContractAnswer.Refusal<int>(reader, "work.report.read"));
        Assert.Contains(ProbeTick.Title, answer.Text);

        // Подпись живёт только внутри задачи: вне её автор — снова просто «Система».
        await scope.ServiceProvider.GetRequiredService<IModuleActivityLog>()
            .RecordAsync(ProbeModuleActivity.InvoicePaid, targetLabel: "вне задачи");
        var outside = (await scope.ServiceProvider.GetRequiredService<IActivityLog>()
                .ReadAsync(0, 50, ActivityVisibility.Whole, ProbeModuleActivity.InvoicePaid.Code))
            .Single(r => r.TargetLabel == "вне задачи");
        Assert.Equal(ActivityActor.System.Name, outside.ActorName);
    }

    /// <summary>Отказ задачи такта — общесистемным уведомлением, а не в чей-то личный колокольчик.</summary>
    [Fact]
    public async Task Отказ_задачи_такта_уходит_общесистемным_уведомлением()
    {
        Gate.FailWith = "Окно подачи не прочитать";
        await Service(host.Clock, new Recording()).TickAsync(default);
        var job = Assert.Single(await JobsAsync());
        await Gate.NextAsync();

        var failed = await StatusAsync(job.Id, JobStatus.Failed);
        Assert.Equal("Окно подачи не прочитать", failed.Error);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notice = await WaitAsync(() => db.Notifications.AsNoTracking()
            .FirstOrDefaultAsync(n => n.Title == "Ошибка: " + ProbeTick.Title));
        Assert.Null(notice.UserId);
        Assert.Equal(NotificationAudiences.SystemManage, notice.Audience);
    }

    /// <summary>
    /// Проверка обработчиков стоит на СТАРТЕ приложения, а не только в каталоге: такт без обработчика
    /// отказывал бы на каждом сроке уведомлением раз в период.
    /// </summary>
    [Fact]
    public void Такт_без_обработчика_не_даёт_приложению_стартовать()
    {
        var orphan = new TickingModule("costs",
            new ModuleTick("orphan", "Никем не выполняемый", "costs.никто-не-умеет", TimeSpan.FromMinutes(5)));
        using var broken = host.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton(new ModuleTickCatalog([orphan]))));

        var refusal = Record.Exception(() => broken.Services);

        Assert.NotNull(refusal);
        var text = Flatten(refusal);
        Assert.Contains("Такт «orphan» («Никем не выполняемый») модуля «costs»", text);
        Assert.Contains("costs.никто-не-умеет", text);
    }

    // ── Помощники ────────────────────────────────────────────────────────────

    private ModuleTickService Service(TimeProvider clock, ILogger<ModuleTickService> log) =>
        new(host.Services.GetRequiredService<IServiceScopeFactory>(),
            host.Services.GetRequiredService<ModuleTickCatalog>(), log, clock);

    private async Task<List<Job>> JobsAsync()
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Jobs.AsNoTracking()
            .Where(j => j.TargetId == ProbeTick.Target)
            .OrderBy(j => j.CreatedAt)
            .ToListAsync();
    }

    private Task<Job> SucceededAsync(Guid jobId) => StatusAsync(jobId, JobStatus.Succeeded);

    private async Task<Job> StatusAsync(Guid jobId, JobStatus status)
    {
        return await WaitAsync(async () =>
        {
            using var scope = host.Services.CreateScope();
            var job = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Jobs.AsNoTracking()
                .FirstAsync(j => j.Id == jobId);
            return job.Status == status ? job : null;
        });
    }

    private static async Task<T> WaitAsync<T>(Func<Task<T?>> probe) where T : class
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await probe() is { } found) return found;
            await Task.Delay(50);
        }
        throw new Xunit.Sdk.XunitException("Не дождались за 30 с.");
    }

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException) parts.Add(e.Message);
        if (ex is AggregateException agg) parts.AddRange(agg.Flatten().InnerExceptions.Select(Flatten));
        return string.Join(" | ", parts);
    }

    /// <summary>Журнал сервера службы: сюда попадает всё уровнем предупреждения и выше.</summary>
    private sealed class Recording : ILogger<ModuleTickService>
    {
        public List<string> Problems { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) lock (Problems) Problems.Add(formatter(state, exception));
        }
    }
}
