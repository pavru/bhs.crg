using System.Collections.Concurrent;
using BHS.CRG.Application.Common;
using BHS.CRG.Infrastructure.Storage;

namespace BHS.CRG.Tests.Integration;

/// <summary>In-memory blob storage substitute for integration tests.</summary>
public class FakeBlobStorage : IBlobStorage
{
    private readonly ConcurrentDictionary<string, byte[]> _store = new();

    /// <summary>
    /// Хранилище «не отвечает»: размер не узнать, удаление падает (issue #741).
    ///
    /// Заведено потому, что настоящий MinIO на недоступность и на отсутствие объекта отвечает
    /// одинаково — <c>GetSizeAsync</c> возвращает <c>null</c> в обоих случаях, — а последствия
    /// разные: приняв связь за отсутствие, уборка сняла бы записи реестра у живых файлов и сделала
    /// бы их недоступными навсегда. Воспроизвести это без флага нечем.
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

    public async Task<string> UploadAsync(string fileName, Stream content, string contentType, CancellationToken ct = default)
    {
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
        if (_store.TryGetValue(blobPath, out var bytes))
            return Task.FromResult<Stream>(new MemoryStream(bytes));
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

    public Task<long?> GetSizeAsync(string blobPath, CancellationToken ct = default)
        => Task.FromResult(!Offline && _store.TryGetValue(blobPath, out var bytes)
            ? bytes.LongLength : (long?)null);

    /// <summary>Для тестов best-effort очистки осиротевших blob'ов — проверить, что путь реально удалён/не существовал.</summary>
    public bool Exists(string blobPath) => _store.ContainsKey(blobPath);
}
