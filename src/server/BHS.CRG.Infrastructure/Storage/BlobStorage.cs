using BHS.CRG.Application.Common;
using Minio;
using Minio.DataModel.Args;

namespace BHS.CRG.Infrastructure.Storage;

public class MinIOBlobStorage(IMinioClient minio, BlobStorageOptions options) : IBlobStorage
{
    public async Task<string> UploadAsync(string fileName, Stream content, string contentType, CancellationToken ct = default)
    {
        await EnsureBucketAsync(ct);
        var objectName = $"{DateTimeOffset.UtcNow:yyyy/MM/dd}/{Guid.NewGuid()}_{fileName}";
        await PutToMinioAsync(options.Bucket, objectName, content, contentType, ct);
        return $"{options.Bucket}/{objectName}";
    }

    public async Task PutAsync(string blobPath, Stream content, string contentType, CancellationToken ct = default)
    {
        var (bucket, objectName) = ParsePath(blobPath);
        await EnsureBucketAsync(ct);
        await PutToMinioAsync(bucket, objectName, content, contentType, ct);
    }

    private async Task PutToMinioAsync(string bucket, string objectName, Stream content, string contentType, CancellationToken ct)
    {
        long size = content.CanSeek ? content.Length : -1;
        await minio.PutObjectAsync(new PutObjectArgs()
            .WithBucket(bucket)
            .WithObject(objectName)
            .WithStreamData(content)
            .WithObjectSize(size)
            .WithContentType(contentType), ct);
    }

    public async Task<Stream> DownloadAsync(string blobPath, CancellationToken ct = default)
    {
        var (bucket, obj) = ParsePath(blobPath);
        var ms = new MemoryStream();
        await minio.GetObjectAsync(new GetObjectArgs()
            .WithBucket(bucket)
            .WithObject(obj)
            .WithCallbackStream(s => s.CopyTo(ms)), ct);
        ms.Position = 0;
        return ms;
    }

    public Task DeleteAsync(string blobPath, CancellationToken ct = default)
    {
        var (bucket, obj) = ParsePath(blobPath);
        return minio.RemoveObjectAsync(new RemoveObjectArgs()
            .WithBucket(bucket)
            .WithObject(obj), ct);
    }

    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        var exists = await minio.BucketExistsAsync(
            new BucketExistsArgs().WithBucket(options.Bucket), ct);
        if (!exists)
            await minio.MakeBucketAsync(
                new MakeBucketArgs().WithBucket(options.Bucket), ct);
    }

    private static (string bucket, string obj) ParsePath(string blobPath)
    {
        var idx = blobPath.IndexOf('/');
        return idx < 0
            ? throw new ArgumentException("Invalid blob path", nameof(blobPath))
            : (blobPath[..idx], blobPath[(idx + 1)..]);
    }
}

public class BlobStorageOptions
{
    public string Endpoint { get; set; } = "localhost:9000";
    public string AccessKey { get; set; } = "minioadmin";
    public string SecretKey { get; set; } = "minioadmin";
    public string Bucket { get; set; } = "bhs-crg";
    public bool UseSSL { get; set; }
}
