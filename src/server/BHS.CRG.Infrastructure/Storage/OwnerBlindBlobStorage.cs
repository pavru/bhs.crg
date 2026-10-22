using BHS.CRG.Application.Common;

namespace BHS.CRG.Infrastructure.Storage;

/// <summary>
/// Само хранилище — то, что лежит ПОД обёрткой с реестром (<see cref="RegisteredBlobStorage" />):
/// владельца файла оно не знает и знать не должно (ТЗ CORE-37.2). Перегрузки с владельцем здесь
/// отбрасывают его явно, одним местом на всех, кто стоит в этом положении: настоящее хранилище и его
/// подделки в тестах.
///
/// <para>Базовым классом, а не умолчанием интерфейса: умолчание досталось бы и ОБЁРТКАМ, а обёртка,
/// молча превратившая владельца в ядро, отдаёт резервной копии файлы модулей «отсутствующими».
/// Обёртка реализует перегрузки сама; «владелец мне не нужен» говорит только тот, кто от этого
/// класса унаследован.</para>
/// </summary>
public abstract class OwnerBlindBlobStorage : IBlobStorage
{
    public abstract Task<string> UploadAsync(
        string fileName, Stream content, string contentType, CancellationToken ct = default);
    public abstract Task<Stream> DownloadAsync(string blobPath, CancellationToken ct = default);
    public abstract Task DeleteAsync(string blobPath, CancellationToken ct = default);
    public abstract Task PutAsync(string blobPath, Stream content, string contentType, CancellationToken ct = default);
    public abstract Task<long?> GetSizeAsync(string blobPath, CancellationToken ct = default);

    public Task<string> UploadAsync(
        string fileName, Stream content, string contentType, string owner, CancellationToken ct = default)
        => UploadAsync(fileName, content, contentType, ct);

    public Task PutAsync(string blobPath, Stream content, string contentType, string owner, CancellationToken ct = default)
        => PutAsync(blobPath, content, contentType, ct);

    public Task<Stream> DownloadAsync(string blobPath, string owner, CancellationToken ct = default)
        => DownloadAsync(blobPath, ct);

    public Task DeleteAsync(string blobPath, string owner, CancellationToken ct = default)
        => DeleteAsync(blobPath, ct);

    public Task<long?> GetSizeAsync(string blobPath, string owner, CancellationToken ct = default)
        => GetSizeAsync(blobPath, ct);

    public Task<IReadOnlySet<string>> FindAbsentAsync(
        IEnumerable<string> blobPaths, string owner, CancellationToken ct = default)
        => ((IBlobStorage)this).FindAbsentAsync(blobPaths, ct);
}
