using BHS.CRG.Domain.Jobs;
using BHS.CRG.Infrastructure.Jobs;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Jobs;

/// <summary>Постановка фоновой задачи в очередь и запрос «мои активные». Со СВОЕЙ <see cref="JobQueue"/>
/// (не DI-синглтоном) — чтобы фоновый сервис фикстуры не подхватил и не завершил задачу до проверки.</summary>
[Collection("Integration")]
public class JobServiceTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task EnqueueAsync_CreatesQueuedJob_VisibleInActiveForOwnerOnly()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var svc = new JobService(db, new JobQueue());
        var userId = Guid.NewGuid();

        var jobId = await svc.EnqueueAsync(JobKind.RecognizeGostSet, userId, Guid.NewGuid(), "Распознавание листов PDF", null, default);

        var active = await svc.GetActiveForUserAsync(userId, default);
        var job = Assert.Single(active);
        Assert.Equal(jobId, job.Id);
        Assert.Equal("Queued", job.Status);
        Assert.Equal("RecognizeGostSet", job.Kind);
        Assert.Equal("Распознавание листов PDF", job.Title);

        // Чужие задачи не видны.
        Assert.Empty(await svc.GetActiveForUserAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task GetActiveForUser_ExcludesFinishedJobs()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var svc = new JobService(db, new JobQueue());
        var userId = Guid.NewGuid();

        var job = Job.Create(JobKind.RecognizeTable, userId, Guid.NewGuid(), "Распознавание таблицы");
        job.Start();
        job.Succeed();
        db.Jobs.Add(job);
        await db.SaveChangesAsync();

        Assert.Empty(await svc.GetActiveForUserAsync(userId, default));
    }

    [Fact]
    public async Task CancelAsync_CancelsQueued_RefusesRunningAndForeign()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var svc = new JobService(db, new JobQueue());
        var userId = Guid.NewGuid();

        // Из очереди — отменяется, уходит из активных.
        var queuedId = await svc.EnqueueAsync(JobKind.RecognizeGostSet, userId, Guid.NewGuid(), "Q", null, default);
        Assert.True(await svc.CancelAsync(queuedId, userId, default));
        Assert.Empty(await svc.GetActiveForUserAsync(userId, default));

        // Уже выполняется — отмена отклоняется (добегает до конца).
        var running = Job.Create(JobKind.RecognizeGostSet, userId, Guid.NewGuid(), "R");
        running.Start();
        db.Jobs.Add(running);
        await db.SaveChangesAsync();
        Assert.False(await svc.CancelAsync(running.Id, userId, default));

        // Чужую задачу отменить нельзя.
        var foreignId = await svc.EnqueueAsync(JobKind.RecognizeGostSet, userId, Guid.NewGuid(), "F", null, default);
        Assert.False(await svc.CancelAsync(foreignId, Guid.NewGuid(), default));
    }
}
