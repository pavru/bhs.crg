using System.Collections.Concurrent;
using BHS.CRG.Application.Common;
using BHS.CRG.Infrastructure.Storage;

namespace BHS.CRG.Tests.Integration;

/// <summary>In-memory blob storage substitute for integration tests.</summary>
public class FakeBlobStorage : IBlobStorage
{
    private readonly ConcurrentDictionary<string, byte[]> _store = new();

    /// <summary>
    /// Хранилище «не отвечает»: размер не узнать, чтение и удаление падают (issue #741).
    ///
    /// Размер при этом — отказ, а не <c>null</c>, как у настоящего хранилища с issue #1427. Прежде
    /// оно на недоступность и на отсутствие объекта отвечало одинаково, <c>null</c>, и подделка
    /// повторяла это — а последствия разные: приняв связь за отсутствие, уборка сняла бы записи
    /// реестра у живых файлов, а оценка копии показала бы все файлы пропавшими.
    ///
    /// <para>Сбрасывать обязательно (<c>try/finally</c>): подделка — синглтон на всю коллекцию.</para>
    /// </summary>
    public bool Offline { get; set; }

    /// <summary>
    /// Размер отдаём, а удаление роняем. Отдельно от <see cref="Offline" />: связь может быть, а
    /// прав на удаление не быть, и именно в этом случае терялась правда об освобождённом месте —
    /// байты складывались до цикла удаления, а сбои цикла проглатывались.
    /// </summary>
    public bool FailDeletes { get; set; }

    /// <summary>
    /// Хранилище отвечает, но о каждом объекте говорит «нет» — так выглядит пустой или чужой бакет
    /// (перепутанная настройка, хранилище поднято заново). Отказом это не является, и отличить его
    /// от пропавших файлов может только проба по живым объектам (issue #1427).
    /// <para>Сбрасывать обязательно (<c>try/finally</c>): подделка — синглтон на всю коллекцию.</para>
    /// </summary>
    public bool Blank { get; set; }

    /// <summary>
    /// Путь, о размере которого хранилище отказывает всегда, хотя остальные отдаёт (issue #1427):
    /// ключ, которого оно не принимает, или объект, на котором оно падает.
    /// <para>Сбрасывать обязательно (<c>try/finally</c>): подделка — синглтон на всю коллекцию.</para>
    /// </summary>
    public string? RefuseSizeOf { get; set; }

    /// <summary>
    /// Путь, чьё содержимое обрывается посреди чтения: открыть файл удаётся, а дочитать — нет
    /// (issue #1425). Так ведёт себя хранилище, оборвавшее соединение после первых байтов.
    /// <para>Сбрасывать обязательно (<c>try/finally</c>): подделка — синглтон на всю коллекцию.</para>
    /// </summary>
    public string? BreakBodyOf { get; set; }

    /// <summary>Сколько файлов выгружено за жизнь подделки — чтобы проверить, что отказ пришёл ДО выгрузки.</summary>
    public int Uploads => _uploads;
    private int _uploads;

    public async Task<string> UploadAsync(string fileName, Stream content, string contentType, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _uploads);
        // Раскладка — та же, что у настоящего хранилища (issue #672). Прежняя выдумка
        // «fake/{guid}/{имя}» была не безобидной: сбор реестра опознаёт пути ПО ФОРМЕ, и подделка,
        // порождающая другую форму, оставляла бы тесты зелёными при выражении, которое на живых
        // данных не находит ничего. Ровно это один раз и произошло.
        var path = $"fake/{BlobPathShape.NewObjectName(fileName)}";
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        _store[path] = ms.ToArray();
        return path;
    }

    public Task<Stream> DownloadAsync(string blobPath, CancellationToken ct = default)
    {
        // Режимы недоступности — и для чтения: подделка, у которой размер «нет», а файл отдаётся,
        // проверяла бы поведение, которого у настоящего хранилища не бывает.
        if (Offline) throw new IOException("Хранилище недоступно");
        if (!Blank && _store.TryGetValue(blobPath, out var bytes))
            return Task.FromResult<Stream>(blobPath == BreakBodyOf ? new BrokenBodyStream(bytes) : new MemoryStream(bytes));
        // Не доменный отказ намеренно (issue #691): отсутствующий файл — это битая ссылка, то есть
        // дефект, а не то, что пользователь может исправить. Настоящее хранилище тоже отвечает здесь
        // своим исключением, и подделка должна вести себя так же.
        throw new KeyNotFoundException($"Blob not found: {blobPath}");
    }

    public Task DeleteAsync(string blobPath, CancellationToken ct = default)
    {
        if (Offline || FailDeletes) throw new IOException("Хранилище недоступно");
        _store.TryRemove(blobPath, out _);
        return Task.CompletedTask;
    }

    public async Task PutAsync(string blobPath, Stream content, string contentType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        _store[blobPath] = ms.ToArray();
    }

    /// <summary>
    /// Сколько раз хранилище спросили о размере — чтобы проверить, что о пути, которого нет в реестре,
    /// его не спрашивают вовсе (issue #1439).
    /// </summary>
    public int SizeQueries => _sizeQueries;
    private int _sizeQueries;

    public Task<long?> GetSizeAsync(string blobPath, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _sizeQueries);
        if (Offline || blobPath == RefuseSizeOf)
            throw new BlobStorageUnavailableException($"Хранилище не ответило ({blobPath})", new IOException("Хранилище недоступно"));
        return Task.FromResult(!Blank && _store.TryGetValue(blobPath, out var bytes) ? bytes.LongLength : (long?)null);
    }

    /// <summary>Отдаёт первую половину файла и обрывается — см. <see cref="BreakBodyOf" />.</summary>
    private sealed class BrokenBodyStream(byte[] bytes) : Stream
    {
        private bool _sent;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_sent) throw new IOException("Хранилище оборвало соединение посреди файла");
            _sent = true;
            var n = Math.Min(count, Math.Max(1, bytes.Length / 2));
            Array.Copy(bytes, 0, buffer, offset, n);
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Для тестов best-effort очистки осиротевших blob'ов — проверить, что путь реально удалён/не существовал.</summary>
    public bool Exists(string blobPath) => _store.ContainsKey(blobPath);
}
