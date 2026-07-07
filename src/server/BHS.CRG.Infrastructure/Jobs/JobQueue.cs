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

    public void Enqueue(Guid jobId) => _channel.Writer.TryWrite(jobId);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
