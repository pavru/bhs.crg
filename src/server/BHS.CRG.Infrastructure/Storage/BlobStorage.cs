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
        // Тело читает асинхронный обратный вызов — с токеном. Перегрузка с одним параметром читала
        // его синхронно (`s.CopyTo(ms)`), а отмена у клиента действует только до заголовков ответа:
        // хранилище, отдавшее заголовки и замершее посреди тела, её не слышало, и поток пула стоял
        // в чтении, пока хранилище не оживёт. Читает файлы одно это место, так что стоял бы любой
        // читатель — от значка вкладки до сборки комплекта.
        //
        // ⚠️ Годится это, только пока клиент обратного вызова ДОЖИДАЕТСЯ: иначе управление вернётся
        // после первых байтов, и наружу уйдёт поток короче файла — без единой ошибки. В Minio 7.0.0
        // дожидается (сверено по сборке: ExecuteTaskCoreAsync); и это, и саму отмену при обновлении
        // пакета сторожит BlobDownloadCancellationTests — настоящим клиентом на поддельном хранилище.
        await minio.GetObjectAsync(new GetObjectArgs()
            .WithBucket(bucket)
            .WithObject(obj)
            .WithCallbackStream((s, c) => CopyBodyAsync(s, ms, c)), ct);
        ms.Position = 0;
        return ms;
    }

    /// <summary>
    /// Переносит тело ответа в память и не ждёт замолчавшее хранилище дольше
    /// <see cref="BlobStorageOptions.ReadStallTimeout"/>.
    ///
    /// Отменяемость сама зависания не лечит: читатель без токена — или с токеном остановки
    /// приложения — ждал бы замершее тело до перезапуска. Срок стоит на ТИШИНЕ, а не на всём
    /// чтении: большой файл по медленному каналу читается сколько нужно, пока байты идут, а
    /// общий срок пришлось бы подбирать под самый большой файл и самый медленный канал разом.
    ///
    /// Истёкший срок — <see cref="TimeoutException"/>, а не отмена: вызывающий ничего не отменял,
    /// и «отменено» в журнале указывало бы на него, а не на хранилище.
    /// </summary>
    private async Task CopyBodyAsync(Stream body, MemoryStream target, CancellationToken ct)
    {
        using var silence = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var buffer = new byte[81920];
        try
        {
            while (true)
            {
                silence.CancelAfter(options.ReadStallTimeout);
                var read = await body.ReadAsync(buffer, silence.Token);
                if (read == 0) return;
                target.Write(buffer, 0, read);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Хранилище замолчало посреди файла: за {options.ReadStallTimeout.TotalSeconds:0.#} с не пришло ни байта.");
        }
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

    /// <summary>
    /// Сколько чтение файла ждёт очередных байтов тела, прежде чем признать хранилище замершим.
    /// Это срок тишины, не срок чтения: он заводится заново с каждой пришедшей порцией. Минута —
    /// с большим запасом: живой Garage отдаёт блок за миллисекунды.
    /// </summary>
    public TimeSpan ReadStallTimeout { get; set; } = TimeSpan.FromSeconds(60);
}
