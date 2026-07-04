using System.Collections.Concurrent;
using BHS.CRG.Application.Common;

namespace BHS.CRG.Tests.Integration;

/// <summary>In-memory blob storage substitute for integration tests.</summary>
public class FakeBlobStorage : IBlobStorage
{
    private readonly ConcurrentDictionary<string, byte[]> _store = new();

    public async Task<string> UploadAsync(string fileName, Stream content, string contentType, CancellationToken ct = default)
    {
        var path = $"fake/{Guid.NewGuid():N}/{fileName}";
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        _store[path] = ms.ToArray();
        return path;
    }

    public Task<Stream> DownloadAsync(string blobPath, CancellationToken ct = default)
    {
        if (_store.TryGetValue(blobPath, out var bytes))
            return Task.FromResult<Stream>(new MemoryStream(bytes));
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
