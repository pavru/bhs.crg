using System.Collections.Concurrent;
using BHS.CRG.Application.Common;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Хранилище-шпион поверх настоящей цепочки (реестр → подделка): пишет каждое ЧТЕНИЕ и умеет
/// подменить его исход для одного пути — отдать файл длиннее предела, отказать, вызвать действие
/// теста до чтения.
///
/// <para>Стоит НАД реестром, а не под ним: попытка чтения отменённым запросом падает уже в реестре,
/// на запросе к базе, и до подделки не доходит. Сторож отмены считает именно попытки — «после отмены
/// сборка пошла за следующим файлом», а не «следующий файл выдан».</para>
/// </summary>
public sealed class GenerationBlobSpy(IBlobStorage inner) : IBlobStorage
{
    /// <summary>Чтения по порядку — путь каждой попытки, удачной или нет.</summary>
    public ConcurrentQueue<string> Reads { get; } = new();

    /// <summary>Пути, чьё содержимое подменяется потоком длиной <see cref="OversizeLength" />.</summary>
    public ConcurrentDictionary<string, bool> Oversize { get; } = new();

    /// <summary>Пути, чтение которых отказывает, как у недоступного хранилища.</summary>
    public ConcurrentDictionary<string, bool> Refuse { get; } = new();

    /// <summary>Действие теста перед чтением пути — например, отменить запрос.</summary>
    public ConcurrentDictionary<string, Action> BeforeRead { get; } = new();

    /// <summary>На байт длиннее предела генерации для ассетов и вложений (100 МБ).</summary>
    public const long OversizeLength = 100L * 1024 * 1024 + 1;

    /// <summary>Снять все подмены — шпион живёт столько же, сколько хост теста.</summary>
    public void Reset()
    {
        Reads.Clear();
        Oversize.Clear();
        Refuse.Clear();
        BeforeRead.Clear();
    }

    public Task<Stream> DownloadAsync(string blobPath, CancellationToken ct = default)
        => Spy(blobPath, () => inner.DownloadAsync(blobPath, ct));

    public Task<Stream> DownloadAsync(string blobPath, string owner, CancellationToken ct = default)
        => Spy(blobPath, () => inner.DownloadAsync(blobPath, owner, ct));

    private async Task<Stream> Spy(string blobPath, Func<Task<Stream>> read)
    {
        Reads.Enqueue(blobPath);
        if (BeforeRead.TryGetValue(blobPath, out var before)) before();
        if (Refuse.ContainsKey(blobPath)) throw new IOException($"Хранилище недоступно ({blobPath})");
        var stream = await read();
        if (!Oversize.ContainsKey(blobPath)) return stream;
        await stream.DisposeAsync();
        return new ZeroStream(OversizeLength);
    }

    public Task<string> UploadAsync(string fileName, Stream content, string contentType, CancellationToken ct = default)
        => inner.UploadAsync(fileName, content, contentType, ct);

    public Task<string> UploadAsync(string fileName, Stream content, string contentType, string owner, CancellationToken ct = default)
        => inner.UploadAsync(fileName, content, contentType, owner, ct);

    public Task DeleteAsync(string blobPath, CancellationToken ct = default) => inner.DeleteAsync(blobPath, ct);

    public Task DeleteAsync(string blobPath, string owner, CancellationToken ct = default)
        => inner.DeleteAsync(blobPath, owner, ct);

    public Task PutAsync(string blobPath, Stream content, string contentType, CancellationToken ct = default)
        => inner.PutAsync(blobPath, content, contentType, ct);

    public Task PutAsync(string blobPath, Stream content, string contentType, string owner, CancellationToken ct = default)
        => inner.PutAsync(blobPath, content, contentType, owner, ct);

    public Task<long?> GetSizeAsync(string blobPath, string owner, CancellationToken ct = default)
        => inner.GetSizeAsync(blobPath, owner, ct);

    public Task<long?> GetSizeAsync(string blobPath, CancellationToken ct = default)
        => inner.GetSizeAsync(blobPath, ct);

    public Task<IReadOnlySet<string>> FindAbsentAsync(IEnumerable<string> blobPaths, string owner, CancellationToken ct = default)
        => inner.FindAbsentAsync(blobPaths, owner, ct);

    public Task<IReadOnlySet<string>> FindAbsentAsync(IEnumerable<string> blobPaths, CancellationToken ct = default)
        => inner.FindAbsentAsync(blobPaths, ct);

    /// <summary>Нули заданной длины, не занимая памяти: файл «больше предела» без 100 МБ в тесте.</summary>
    private sealed class ZeroStream(long length) : Stream
    {
        private long _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _position);
            if (n <= 0) return 0;
            Array.Clear(buffer, offset, n);
            _position += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
