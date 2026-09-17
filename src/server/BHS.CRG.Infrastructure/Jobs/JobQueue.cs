using System.Threading.Channels;

namespace BHS.CRG.Infrastructure.Jobs;

/// <summary>
/// In-process очередь id фоновых задач (singleton). Источник истины — таблица Jobs в БД; здесь только
/// сигнал «есть работа» для hosted-сервиса. Неограниченная (задач мало, ставятся по явному действию).
/// При рестарте очередь теряется — зависшие Queued/Running помечаются Failed на старте (см. Program.cs).
/// </summary>
public sealed class JobQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true });

    // Поставлено и ещё не доработано — включая ту задачу, что выполняется сейчас.
    private int _outstanding;

    public void Enqueue(Guid jobId)
    {
        Interlocked.Increment(ref _outstanding);
        if (!_channel.Writer.TryWrite(jobId)) Interlocked.Decrement(ref _outstanding);
    }

    /// <summary>
    /// Нет ни поставленных, ни выполняющихся задач (issue #928).
    ///
    /// Нужно тому, кто трогает базу целиком: задача, дорабатывающая в фоне, пишет строки после
    /// чужой очистки. В тестах это давало плавающую взаимную блокировку с TRUNCATE — и, хуже
    /// блокировки, строки прошлого теста в базе следующего.
    /// </summary>
    public bool IsIdle => Volatile.Read(ref _outstanding) == 0;

    /// <summary>
    /// Разбирает очередь, отдавая каждую задачу обработчику. Задача считается доработанной, когда
    /// обработчик вернул управление КАК УГОДНО — успехом, досрочным выходом или исключением: иначе
    /// одна упавшая задача навсегда сделала бы очередь «занятой».
    ///
    /// Исключение обработчика учитывается и выходит наружу, заканчивая разбор: свои ошибки обработчик
    /// обязан ловить сам — очередь не решает, какую ошибку можно пережить.
    /// </summary>
    public async Task ProcessAllAsync(Func<Guid, Task> handle, CancellationToken ct)
    {
        await foreach (var jobId in _channel.Reader.ReadAllAsync(ct))
        {
            try { await handle(jobId); }
            finally { Interlocked.Decrement(ref _outstanding); }
        }
    }
}
