using System.Net;
using System.Net.Sockets;
using System.Text;
using BHS.CRG.Infrastructure.Storage;
using Minio;

namespace BHS.CRG.Tests.Storage;

/// <summary>
/// Размер объекта: <c>null</c> — только когда хранилище ответило «такого объекта нет» (issue #1427).
///
/// Прежде <c>GetSizeAsync</c> возвращал <c>null</c> на ЛЮБОЙ отказ, и лежащее хранилище, 403 или
/// ошибка сервера выглядели пропавшим файлом: оценка копии показывала архив, в котором пропало всё,
/// вместо отказа. Различать отказы может только этот слой — выше ответ хранилища уже не виден.
///
/// Проверяется НАСТОЯЩИМ клиентом на поддельном хранилище, как и чтение тела
/// (<see cref="BlobDownloadCancellationTests" />): какой отказ клиент Minio выдаёт на какой ответ —
/// свойство чужой сборки, и подставной клиент проверял бы наше представление о ней.
/// </summary>
public class BlobSizeTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Объект_на_месте_размер_из_заголовка()
    {
        using var storage = new StatusStorage("200 OK", contentLength: 4321);
        using var client = Client(storage.Port);

        Assert.Equal(4321, await Blobs(client).GetSizeAsync("bucket/file.bin").WaitAsync(Limit));
    }

    [Fact]
    public async Task Объекта_нет_это_null_а_не_отказ()
    {
        using var storage = new StatusStorage("404 Not Found");
        using var client = Client(storage.Port);

        Assert.Null(await Blobs(client).GetSizeAsync("bucket/file.bin").WaitAsync(Limit));
    }

    [Theory]
    [InlineData("403 Forbidden")]
    [InlineData("500 Internal Server Error")]
    [InlineData("503 Service Unavailable")]
    public async Task Отказ_хранилища_это_отказ_а_не_пропавший_файл(string status)
    {
        using var storage = new StatusStorage(status);
        using var client = Client(storage.Port);

        var failure = await Record.ExceptionAsync(() => Blobs(client).GetSizeAsync("bucket/file.bin").WaitAsync(Limit));

        Assert.True(failure is not null, $"хранилище ответило {status}, а размер вернулся как «объекта нет»");
        Assert.IsNotType<TimeoutException>(failure);
    }

    [Fact]
    public async Task Бакета_нет_это_отказ_а_не_пропавший_файл()
    {
        // Чужой или не созданный бакет — перепутанная настройка, а не пропавшие файлы: объявив их
        // пропавшими, оценка копии показала бы пустой архив, а уборка сняла бы записи у живых.
        using var storage = new StatusStorage("404 Not Found", bucketExists: false);
        using var client = Client(storage.Port);

        var failure = await Record.ExceptionAsync(() => Blobs(client).GetSizeAsync("bucket/file.bin").WaitAsync(Limit));

        Assert.True(failure is not null, "бакета нет, а размер вернулся как «объекта нет»");
        Assert.IsNotType<TimeoutException>(failure);
    }

    [Fact]
    public async Task Хранилище_не_слушает_это_отказ_а_не_пропавший_файл()
    {
        // Порт заняли и тут же освободили: подключение к нему получает отказ, как к лежащему хранилищу.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var client = Client(port);

        var failure = await Record.ExceptionAsync(() => Blobs(client).GetSizeAsync("bucket/file.bin").WaitAsync(Limit));

        Assert.True(failure is not null, "до хранилища не достучаться, а размер вернулся как «объекта нет»");
        Assert.IsNotType<TimeoutException>(failure);
    }

    [Fact]
    public async Task Отмена_остаётся_отменой()
    {
        using var storage = new StatusStorage("404 Not Found");
        using var client = Client(storage.Port);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Blobs(client).GetSizeAsync("bucket/file.bin", cts.Token).WaitAsync(Limit));
    }

    // ── Обвязка ──────────────────────────────────────────────────────────────────

    private static IMinioClient Client(int port) => new MinioClient()
        .WithEndpoint("127.0.0.1", port)
        .WithCredentials("GK0123456789abcdef01234567", "secret")
        .WithSSL(false)
        .Build();

    private static MinIOBlobStorage Blobs(IMinioClient client) =>
        new(client, new BlobStorageOptions { Bucket = "bucket" });

    /// <summary>
    /// Поддельное хранилище, которое на HEAD объекта отвечает заданным статусом — без тела, как
    /// настоящее (у ответа на HEAD тела не бывает, и клиент разбирает отказ по одному статусу). На
    /// всё прочее — 501: появись у клиента запрос другого вида, тест покраснеет этим кодом.
    ///
    /// <para>⚠️ HEAD самого бакета отвечается отдельно, и это не подробность: получив 404 на объект,
    /// клиент Minio 7.0.0 переспрашивает бакет и по ответу выбирает отказ — «объекта нет» или «бакета
    /// нет». Первая редакция стенда отвечала на оба одинаково, и клиент честно говорил «бакета нет».
    /// Как отвечает живой Garage (объекта нет — <c>ObjectNotFoundException</c>, бакета нет —
    /// <c>BucketNotFoundException</c>, чужой ключ — <c>AccessDeniedException</c>), сверено на
    /// хранилище дев-стенда.</para>
    /// </summary>
    private sealed class StatusStorage : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly List<TcpClient> _clients = [];
        private readonly string _status;
        private readonly long _contentLength;
        private readonly bool _bucketExists;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public StatusStorage(string status, long contentLength = 0, bool bucketExists = true)
        {
            (_status, _contentLength, _bucketExists) = (status, contentLength, bucketExists);
            _listener.Start();
            _ = AcceptLoopAsync();
        }

        private async Task AcceptLoopAsync()
        {
            while (true)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); }
                catch { return; }
                lock (_clients) _clients.Add(client);
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[8192];
                while (true)
                {
                    var text = new StringBuilder();
                    while (!text.ToString().Contains("\r\n\r\n"))
                    {
                        var n = await stream.ReadAsync(buffer, _stop.Token);
                        if (n == 0) return;
                        text.Append(Encoding.ASCII.GetString(buffer, 0, n));
                    }
                    var parts = text.ToString().Split("\r\n")[0].Split(' ');
                    var path = parts.Length > 1 ? parts[1].Split('?')[0] : "";
                    var head = parts[0] == "HEAD";
                    var response = head && path is "/bucket" or "/bucket/"
                        ? $"HTTP/1.1 {(_bucketExists ? "200 OK" : "404 Not Found")}\r\nContent-Length: 0\r\n\r\n"
                        : head && path.StartsWith("/bucket/", StringComparison.Ordinal)
                        ? $"HTTP/1.1 {_status}\r\n" +
                          $"Content-Length: {_contentLength}\r\n" +
                          "Content-Type: application/octet-stream\r\n" +
                          "ETag: \"0123456789abcdef0123456789abcdef\"\r\n" +
                          "Last-Modified: Wed, 14 Oct 2026 00:00:00 GMT\r\n\r\n"
                        : "HTTP/1.1 501 Not Implemented\r\nContent-Length: 0\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
                }
            }
            catch
            {
                // Тест закончился или клиент оборвал соединение — подделке сообщать об этом некому.
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            lock (_clients)
                foreach (var client in _clients) client.Dispose();
            _stop.Dispose();
        }
    }
}
