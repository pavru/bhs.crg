namespace BHS.CRG.Application.Common;

public interface IBlobStorage
{
    Task<string> UploadAsync(string fileName, Stream content, string contentType, CancellationToken ct = default);
    Task<Stream> DownloadAsync(string blobPath, CancellationToken ct = default);
    Task DeleteAsync(string blobPath, CancellationToken ct = default);
    /// <summary>Restores a blob to its exact original path (used by backup restore).</summary>
    Task PutAsync(string blobPath, Stream content, string contentType, CancellationToken ct = default);
}
