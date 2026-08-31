using BHS.CRG.Application.Jobs;
using BHS.CRG.Domain.Jobs;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Jobs;

/// <summary>
/// Что старт приложения делает с задачами, не пережившими прошлую остановку (issue #901).
///
/// Очередь живёт В ПАМЯТИ процесса (<see cref="JobQueue" />), записи <see cref="Job" /> — в базе:
/// после остановки задача осталась бы <c>Queued</c>/<c>Running</c> навсегда, потому что подхватить
/// её больше некому. Уборка на старте есть давно (<c>Job.MarkAbandoned</c>, вызов в <c>Program</c>),
/// но проверена не была — а с осью ACT (#898) цена её отказа выросла втрое:
///
/// <list type="number">
/// <item>индикатор показывал бы «выполняется» без конца;</item>
/// <item><c>get_job</c> отвечал бы «Running» тому, кто ждёт итога, — то есть «ждите» тому, кому
/// ждать нечего, и опрашивающий в цикле не остановился бы никогда;</item>
/// <item>защита «задача по этой цели уже идёт» стала бы вечным запретом: один перезапуск посреди
/// сборки — и этот комплект не собрать больше никогда, пока не поправят базу руками. У сборки
/// такой защиты раньше не было вовсе, она появилась в #898.</item>
/// </list>
///
/// Проверка идёт через ПОДЪЁМ ОТДЕЛЬНОГО ХОСТА поверх той же базы: у общей фикстуры старт давно
/// позади, а вызвать уборку напрямую значило бы проверить метод, а не то, что его кто-то зовёт.
/// Всё в одном тесте намеренно — поднять хост стоит секунд, а поведение здесь одно.
/// </summary>
[Collection("Integration")]
public class AbandonedJobsOnStartupTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Guid> SeedAsync(Guid userId, Guid targetId, Action<Job>? state = null)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = Job.Create(JobKind.AssembleDocumentSet, userId, targetId, "Задача");
        state?.Invoke(job);
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    private async Task<Job> LoadAsync(Guid jobId)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
    }

    [Fact]
    public async Task Startup_FailsUnfinishedJobs_KeepsFinishedOnes_AndReleasesTheirTargets()
    {
        var userId = Guid.NewGuid();
        var heldTarget = Guid.NewGuid();

        var queued = await SeedAsync(userId, heldTarget);
        var running = await SeedAsync(userId, Guid.NewGuid(), j => j.Start());
        var succeeded = await SeedAsync(userId, Guid.NewGuid(), j => { j.Start(); j.Succeed(); });
        var failed = await SeedAsync(userId, Guid.NewGuid(), j => { j.Start(); j.Fail("Своя причина."); });
        var cancelled = await SeedAsync(userId, Guid.NewGuid(), j => j.TryCancel());

        // До старта цель занята — иначе проверка ниже не значила бы ничего: она была бы зелёной и
        // на приложении, которое не делает вообще ничего.
        using (var before = fixture.Services.CreateScope())
        {
            var jobs = before.ServiceProvider.GetRequiredService<IJobService>();
            Assert.True(await jobs.HasActiveForTargetAsync(
                userId, heldTarget, default, JobKind.AssembleDocumentSet));
        }

        using var host = fixture.WithWebHostBuilder(_ => { });
        host.CreateClient(); // сам подъём хоста и есть проверяемое событие

        // Незавершённые — в терминальном состоянии, с причиной, по которой понятно, что произошло.
        foreach (var id in new[] { queued, running })
        {
            var job = await LoadAsync(id);
            Assert.Equal(JobStatus.Failed, job.Status);
            Assert.Contains("перезапуск", job.Error ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(job.FinishedAt);
        }

        // Завершённых уборка не касается: иначе история операций переписывалась бы при каждом
        // запуске приложения, а причина давно упавшей задачи подменялась бы на «перезапустили».
        Assert.Equal(JobStatus.Succeeded, (await LoadAsync(succeeded)).Status);
        Assert.Equal("Своя причина.", (await LoadAsync(failed)).Error);
        Assert.Equal(JobStatus.Cancelled, (await LoadAsync(cancelled)).Status);

        // И главное: цель свободна — операцию по ней можно запустить снова.
        using var after = fixture.Services.CreateScope();
        var jobsAfter = after.ServiceProvider.GetRequiredService<IJobService>();
        Assert.False(await jobsAfter.HasActiveForTargetAsync(
            userId, heldTarget, default, JobKind.AssembleDocumentSet));
    }
}
