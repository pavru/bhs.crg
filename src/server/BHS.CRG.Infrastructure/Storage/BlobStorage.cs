using BHS.CRG.Application.Common;
using Minio;
using Minio.DataModel.Args;

namespace BHS.CRG.Infrastructure.Storage;

public class MinIOBlobStorage(IMinioClient minio, BlobStorageOptions options) : IBlobStorage
{
    public async Task<string> UploadAsync(string fileName, Stream content, string contentType, CancellationToken ct = default)
    {
        await EnsureBucketAsync(ct);
        // Раскладка пути — не здесь: её же должен знать разовый сбор реестра и подделка в тестах
        // (issue #672). Держим в одном месте, иначе расходятся молча.
        var objectName = BlobPathShape.NewObjectName(fileName);
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

    public async Task<long?> GetSizeAsync(string blobPath, CancellationToken ct = default)
    {
        var (bucket, obj) = ParsePath(blobPath);
        try
        {
            var stat = await minio.StatObjectAsync(new StatObjectArgs()
                .WithBucket(bucket)
                .WithObject(obj), ct);
            return stat.Size;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Объекта нет (или до него не достучаться) — для оценки это «ноль байт», а не отказ.
            // Отмену пропускаем наружу: она значит, что спрашивать уже некому.
            return null;
        }
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

    /// <summary>
    /// Разбирает путь блоба на бакет и ключ объекта. Бакет ВСЕГДА берётся из конфигурации, а первый
    /// сегмент пути отбрасывается.
    ///
    /// Хранилище у приложения одно — то, что названо в <see cref="BlobStorageOptions.Bucket"/>;
    /// первый сегмент исторически повторяет его имя и информации не несёт. Раньше он попадал в
    /// запрос как есть, то есть адресуемый бакет задавала строка пути, а не конфигурация. Клиент
    /// MinIO работает под учётной записью, которой видны все бакеты инстанса, включая чужие, —
    /// значит выбор бакета не должен зависеть от входных данных ни в одном вызове.
    ///
    /// Побочно это чинит и переименование бакета: путь, записанный под прежним именем, читается из
    /// текущего, потому что ключ объекта от имени бакета не зависит.
    /// </summary>
    private (string bucket, string obj) ParsePath(string blobPath)
    {
        var idx = blobPath.IndexOf('/');
        return idx < 0
            // Framework-тип намеренно (issue #691): путь приходит не из запроса, а из базы
            // (GeneratedFile.BlobPath и соседи). Ответ «исправьте запрос» тут неверен по существу —
            // исправлять нечего, запись битая, и узнать об этом надо из лога.
            ? throw new InvalidOperationException("Некорректный путь к файлу в хранилище.")
            : (options.Bucket, blobPath[(idx + 1)..]);
    }
}

public class BlobStorageOptions
{
    /// <summary>
    /// Именно IPv4-литерал, а не «localhost» (issue #484): на Windows localhost резолвится сначала
    /// в IPv6, где порт-прокси Docker Desktop не слушает и при этом МОЛЧИТ вместо отказа —
    /// первое обращение к хранилищу висело 21 секунду до таймаута TCP. В контейнере адрес всё
    /// равно задаётся переменной окружения (garage:3900), а этот дефолт страхует конфигурации,
    /// где секция BlobStorage не заполнена. Порт 3900 — Garage (issue #885); до 0.160.0 здесь
    /// стоял 9000, порт MinIO.
    /// </summary>
    public string Endpoint { get; set; } = "127.0.0.1:3900";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string Bucket { get; set; } = "bhs-crg";
    public bool UseSSL { get; set; }
}
