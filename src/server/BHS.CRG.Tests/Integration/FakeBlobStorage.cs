using System.Collections.Concurrent;
using BHS.CRG.Application.Common;
using BHS.CRG.Infrastructure.Storage;

namespace BHS.CRG.Tests.Integration;

/// <summary>In-memory blob storage substitute for integration tests.</summary>
public class FakeBlobStorage : IBlobStorage
{
    private readonly ConcurrentDictionary<string, byte[]> _store = new();

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
        _store.TryRemove(blobPath, out _);
        return Task.CompletedTask;
    }

    public async Task PutAsync(string blobPath, Stream content, string contentType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        _store[blobPath] = ms.ToArray();
    }

    /// <summary>Для тестов best-effort очистки осиротевших blob'ов — проверить, что путь реально удалён/не существовал.</summary>
    public bool Exists(string blobPath) => _store.ContainsKey(blobPath);
}
