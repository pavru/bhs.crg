using BHS.CRG.Infrastructure.Jobs;

namespace BHS.CRG.Tests.Jobs;

/// <summary>
/// «Очередь пуста» (issue #928). На этом признаке очистка базы в тестах ждёт фоновые задачи прошлого
/// теста, поэтому ошибиться он может только в одну сторону: ложное «пусто» возвращает гонку с
/// TRUNCATE, а ложное «занято» вешает очистку до предела. Второе — задача, которая упала или вышла
/// досрочно и так и не была учтена доработанной.
/// </summary>
public class JobQueueIdleTests
{
    private static async Task<(Task Worker, CancellationTokenSource Stop)> Run(JobQueue queue, Func<Guid, Task> handle)
    {
        var stop = new CancellationTokenSource();
        var worker = Task.Run(async () =>
        {
            try { await queue.ProcessAllAsync(handle, stop.Token); }
            catch (OperationCanceledException) { }
        });
        await Task.Yield();
        return (worker, stop);
    }

    private static async Task<bool> BecomesIdle(JobQueue queue)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (queue.IsIdle) return true;
            await Task.Delay(10);
        }
        return false;
    }

    [Fact]
    public void Пустая_очередь_пуста()
        => Assert.True(new JobQueue().IsIdle);

    [Fact]
    public void Поставленная_но_не_взятая_задача_занимает_очередь()
    {
        var queue = new JobQueue();
        queue.Enqueue(Guid.NewGuid());

        Assert.False(queue.IsIdle);
    }

    [Fact]
    public async Task Выполняющаяся_задача_занимает_очередь_до_самого_конца()
    {
        // Главный случай: задача уже взята из канала, но ещё пишет в базу — именно она и встречалась
        // с TRUNCATE. Считать очередь пустой по одному каналу значило бы пропустить ровно её.
        var queue = new JobQueue();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (worker, stop) = await Run(queue, async _ => { started.SetResult(); await release.Task; });

        queue.Enqueue(Guid.NewGuid());
        await started.Task;
        await Task.Delay(50);
        Assert.False(queue.IsIdle);

        release.SetResult();
        Assert.True(await BecomesIdle(queue));
        stop.Cancel();
        await worker;
    }

    [Fact]
    public async Task Упавшая_задача_не_оставляет_очередь_занятой()
    {
        var queue = new JobQueue();
        var (worker, stop) = await Run(queue, _ => throw new InvalidOperationException("задача упала"));

        queue.Enqueue(Guid.NewGuid());

        Assert.True(await BecomesIdle(queue), "упавшая задача навсегда оставила очередь занятой");
        // Исключение обработчика выходит наружу и заканчивает разбор — ловить свои ошибки обязан сам
        // обработчик (JobBackgroundService так и делает). Здесь проверяется только учёт.
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker);
        stop.Dispose();
    }

    [Fact]
    public async Task Несколько_задач_учитываются_каждая()
    {
        var queue = new JobQueue();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handled = 0;
        var (worker, stop) = await Run(queue, async _ => { await release.Task; Interlocked.Increment(ref handled); });

        queue.Enqueue(Guid.NewGuid());
        queue.Enqueue(Guid.NewGuid());
        queue.Enqueue(Guid.NewGuid());
        await Task.Delay(50);
        Assert.False(queue.IsIdle);

        release.SetResult();
        Assert.True(await BecomesIdle(queue));
        Assert.Equal(3, handled);
        stop.Cancel();
        await worker;
    }
}
