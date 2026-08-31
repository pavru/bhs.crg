using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Jobs;
using BHS.CRG.Infrastructure.Jobs;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Jobs;

/// <summary>
/// Одна активная задача на цель — правилом базы, а не только проверкой перед постановкой (#900).
///
/// Проверка «по этой цели уже идёт» и вставка — два шага, и между ними есть окно: два запроса,
/// пришедшие одновременно, оба видят «свободно» и ставят по задаче. Для сборки комплекта это две
/// задачи, пишущие один и тот же выход, — ровно та порча, ради которой защита заведена. У человека
/// окно почти недостижимо, у внешнего агента вызов в цикле — обычное дело, и с осью ACT (#898)
/// такой вызывающий появился.
///
/// Гонка воспроизводится буквально: два <c>EnqueueAsync</c> стартуют одновременно, каждый — со своим
/// подключением. Последовательный повтор здесь ничего не доказал бы: он отсекается проверкой и был
/// зелёным задолго до индекса.
/// </summary>
[Collection("Integration")]
public class JobRaceGuardTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Своя область на вызов — как у двух параллельных HTTP-запросов, а не общий контекст.</summary>
    private Task<Guid> EnqueueAsync(JobKind kind, Guid userId, Guid targetId)
    {
        var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Своя очередь, не DI-синглтон: исполнитель фикстуры иначе подхватит задачу и завершит её
        // до проверок — тогда «активной» она уже не будет, и тест мерил бы скорость фона.
        var service = new JobService(db, new JobQueue());
        return Task.Run(async () =>
        {
            try { return await service.EnqueueAsync(kind, userId, targetId, "Задача", null, default); }
            finally { scope.Dispose(); }
        });
    }

    [Fact]
    public async Task TwoSimultaneousEnqueues_ForOneTarget_LeaveExactlyOneJob()
    {
        var userId = Guid.NewGuid();
        var setId = Guid.NewGuid();

        var results = await Task.WhenAll(
            Wrap(EnqueueAsync(JobKind.AssembleDocumentSet, userId, setId)),
            Wrap(EnqueueAsync(JobKind.AssembleDocumentSet, userId, setId)));

        // Один поставил, второму отказано — и отказ именно наш, доменный: он дойдёт до вызывающего
        // как 409 с текстом, а не как внутренняя ошибка с идентификатором запроса.
        Assert.Equal(1, results.Count(r => r.Error is null));
        var refusal = Assert.Single(results.Select(r => r.Error).OfType<Exception>());
        Assert.IsType<ConflictException>(refusal);

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.Jobs.CountAsync(j => j.TargetId == setId));
    }

    /// <summary>
    /// Завершённая задача цель не держит: индекс частичный, иначе комплект нельзя было бы собрать
    /// дважды за всё время его существования.
    /// </summary>
    [Fact]
    public async Task FinishedJob_DoesNotBlockTheNextOne()
    {
        var userId = Guid.NewGuid();
        var setId = Guid.NewGuid();

        var first = await EnqueueAsync(JobKind.AssembleDocumentSet, userId, setId);
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.Jobs.SingleAsync(j => j.Id == first);
            job.Start();
            job.Succeed();
            await db.SaveChangesAsync();
        }

        var second = await EnqueueAsync(JobKind.AssembleDocumentSet, userId, setId);

        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// Отправка почтой — единственное исключение из правила, и оно законно: тот же комплект
    /// отправляют разным получателям двумя действиями подряд. Проверка нужна ровно потому, что
    /// исключение живёт в фильтре индекса, где его легко потерять при следующей правке.
    /// </summary>
    [Fact]
    public async Task Email_IsExemptFromTheRule()
    {
        var userId = Guid.NewGuid();
        var setId = Guid.NewGuid();

        await EnqueueAsync(JobKind.SendEmail, userId, setId);
        await EnqueueAsync(JobKind.SendEmail, userId, setId);

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.Jobs.CountAsync(j => j.TargetId == setId && j.Kind == JobKind.SendEmail));
    }

    /// <summary>Разные операции над одним объектом друг другу не мешают — ключ включает вид.</summary>
    [Fact]
    public async Task DifferentKinds_OnOneTarget_Coexist()
    {
        var userId = Guid.NewGuid();
        var setId = Guid.NewGuid();

        await EnqueueAsync(JobKind.AssembleDocumentSet, userId, setId);
        await EnqueueAsync(JobKind.AuditQualityLinks, userId, setId);

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.Jobs.CountAsync(j => j.TargetId == setId));
    }

    /// <summary>
    /// А вот РАСПОЗНАВАНИЯ разных видов — не «разные операции»: они пишут один и тот же набор
    /// (группировку, распознанные таблицы), и защита перед постановкой отвергает запуск, если по
    /// набору идёт любое из трёх. Ключ индекса обязан быть таким же широким, иначе он подпирает
    /// защиту уже той, что подпирает: одновременные RecognizeGostSet и RecognizeTable прошли бы оба.
    ///
    /// Пары взяты все три — семейство задаётся ПРЕФИКСОМ имени, и проверять надо, что в него попал
    /// каждый, а не тот один, на котором писали правило.
    /// </summary>
    [Theory]
    [InlineData(JobKind.RecognizeGostSet, JobKind.RecognizeTable)]
    [InlineData(JobKind.RecognizeGostSet, JobKind.RecognizeDocument)]
    [InlineData(JobKind.RecognizeTable, JobKind.RecognizeDocument)]
    public async Task RecognitionKinds_ShareOneSlotPerDataset(JobKind first, JobKind second)
    {
        var userId = Guid.NewGuid();
        var fileId = Guid.NewGuid();

        await EnqueueAsync(first, userId, fileId);
        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => EnqueueAsync(second, userId, fileId));
        Assert.Contains("уже выполняется", refusal.Message);

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.Jobs.CountAsync(j => j.TargetId == fileId));
    }

    private static async Task<(Guid Id, Exception? Error)> Wrap(Task<Guid> task)
    {
        try { return (await task, null); }
        catch (Exception ex) { return (Guid.Empty, ex); }
    }
}
