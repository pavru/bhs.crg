using System.Net;
using System.Net.Sockets;
using System.Text;
using BHS.CRG.Infrastructure.Storage;
using Minio;

namespace BHS.CRG.Tests.Storage;

/// <summary>
/// Чтение тела файла из хранилища отменяемо, не ждёт замолчавшее хранилище без срока — и при этом
/// файл приходит целиком либо не приходит вовсе.
///
/// Отмена у клиента хранилища действует только до заголовков ответа. Пока тело читалось синхронно
/// (<c>s.CopyTo(ms)</c>), хранилище, отдавшее заголовки и замершее посреди тела, отмены не слышало:
/// поток пула стоял в чтении, пока хранилище не оживёт, — и так у любого читателя файлов, потому
/// что читает их одно место. Теперь тело читает асинхронный обратный вызов с токеном.
///
/// Проверяется НАСТОЯЩИМ клиентом и настоящими сокетами: поддельное хранилище на петле отвечает на
/// HEAD и GET, как S3, а тело отдаёт так, как велит тест. Подставной поток вместо сети проверял бы
/// наше представление о клиенте, а не клиента: дожидается ли он обратного вызова и доходит ли до
/// него токен — свойства чужой сборки, и на прошлых её версиях первое было неверно.
/// </summary>
public class BlobDownloadCancellationTests
{
    /// <summary>
    /// Срок, за который обязано случиться то, чего тест ждёт. Сам отказ занимает миллисекунды;
    /// запас — на занятый раннер. Не уложились — значит, не случилось вовсе.
    /// </summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Отмена_обрывает_чтение_тела_замершего_посреди_ответа()
    {
        var body = Bytes(256 * 1024);
        using var storage = FakeStorage.StallingAfter(body, sent: 64 * 1024);
        using var client = Client(storage);
        using var cts = new CancellationTokenSource();

        var download = Blobs(client).DownloadAsync("bucket/file.bin", cts.Token);

        // Заголовки и четверть тела ушли, остальное не придёт никогда. Чтение обязано ЖДАТЬ: вернись
        // оно сейчас — с частью файла или с отказом, — проверка отмены ниже ничего бы не значила.
        await storage.Stalled.WaitAsync(Limit);
        await Task.Delay(200);
        Assert.False(download.IsCompleted, "чтение завершилось, хотя хранилище отдало только часть тела");

        cts.Cancel();

        // Именно WhenAny со сроком, а не голое ожидание: глухое к отмене чтение не падает — оно
        // висит, и тест висел бы вместе с ним, пока его не снимет раннер, без слова о причине.
        var finished = await Task.WhenAny(download, Task.Delay(Limit));
        Assert.True(finished == download,
            "чтение тела не услышало отмену: хранилище замерло посреди ответа, и поток пула ждёт его вместе с ним");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
    }

    [Fact]
    public async Task Тело_пришедшее_частями_с_паузами_читается_целиком()
    {
        // Больше блока Garage (1 МиБ) и с паузами между кусками: клиент, не дожидающийся обратного
        // вызова, вернул бы управление после первых байтов — поток короче файла и без единой ошибки.
        var body = Bytes(3 * 1024 * 1024 + 517);
        using var storage = FakeStorage.Dribbling(body, chunk: 256 * 1024, pause: TimeSpan.FromMilliseconds(15));
        using var client = Client(storage);

        await using var stream = await Blobs(client).DownloadAsync("bucket/file.bin").WaitAsync(Limit);

        var read = Assert.IsType<MemoryStream>(stream);
        Assert.Equal(0, read.Position);
        Assert.Equal(body.Length, read.Length);
        Assert.True(body.AsSpan().SequenceEqual(read.ToArray()), "прочитанное отличается от отданного хранилищем");
    }

    [Fact]
    public async Task Замолчавшее_хранилище_без_отмены_обрывает_срок_тишины()
    {
        // Токена у читателя нет — отменить некому. Раньше такое чтение ждало бы до перезапуска.
        using var storage = FakeStorage.StallingAfter(Bytes(256 * 1024), sent: 64 * 1024);
        using var client = Client(storage);
        var blobs = Blobs(client, silence: TimeSpan.FromMilliseconds(300));

        var download = blobs.DownloadAsync("bucket/file.bin");

        var finished = await Task.WhenAny(download, Task.Delay(Limit));
        Assert.True(finished == download, "чтение ждёт замолчавшее хранилище дольше срока тишины");
        // Именно срок, а не отмена: читатель ничего не отменял, и «отменено» указывало бы на него.
        await Assert.ThrowsAsync<TimeoutException>(() => download);
    }

    [Fact]
    public async Task Срок_тишины_не_обрывает_медленное_но_идущее_чтение()
    {
        // Всё чтение — пять секунд, срок тишины — три: общий срок оборвал бы его, а срок тишины
        // заводится заново с каждой порцией и медленному каналу не мешает.
        // ⚠️ Пауза в двенадцать раз короче срока НАРОЧНО: при 150 мс против секунды тест падал на
        // нагруженном раннере CI (два потока тестов на одной машине растянули паузу почти на
        // секунду — CI PR #1331), то есть проверял машину, а не срок.
        var body = Bytes(20 * 16 * 1024);
        using var storage = FakeStorage.Dribbling(body, chunk: 16 * 1024, pause: TimeSpan.FromMilliseconds(250));
        using var client = Client(storage);
        var blobs = Blobs(client, silence: TimeSpan.FromSeconds(3));

        await using var stream = await blobs.DownloadAsync("bucket/file.bin").WaitAsync(Limit);

        Assert.True(body.AsSpan().SequenceEqual(Assert.IsType<MemoryStream>(stream).ToArray()));
    }

    [Fact]
    public async Task Обрыв_соединения_посреди_тела_это_отказ_а_не_короткий_файл()
    {
        // Заголовок обещал 256 КиБ, пришло 64, и хранилище закрыло соединение. Самый прямой путь к
        // «потоку короче файла без единой ошибки»: клиенту достаточно проглотить отказ обратного
        // вызова при статусе 200.
        using var storage = FakeStorage.ClosingAfter(Bytes(256 * 1024), sent: 64 * 1024);
        using var client = Client(storage);

        var download = Blobs(client).DownloadAsync("bucket/file.bin");

        var finished = await Task.WhenAny(download, Task.Delay(Limit));
        Assert.True(finished == download, "чтение не заметило, что хранилище закрыло соединение");
        var failure = await Record.ExceptionAsync(() => download);
        Assert.True(failure is not null, "оборванное на четверти тело вернулось как целый файл");
        Assert.IsNotAssignableFrom<OperationCanceledException>(failure);
    }

    // ── Обвязка ──────────────────────────────────────────────────────────────────

    /// <summary>Клиента освобождает тест: за ним свой HttpClient и соединения к подделке.</summary>
    private static IMinioClient Client(FakeStorage storage) => new MinioClient()
        .WithEndpoint("127.0.0.1", storage.Port)
        .WithCredentials("GK0123456789abcdef01234567", "secret")
        .WithSSL(false)
        .Build();

    private static MinIOBlobStorage Blobs(IMinioClient client, TimeSpan? silence = null)
    {
        var options = new BlobStorageOptions { Bucket = "bucket" };
        if (silence is { } limit) options.ReadStallTimeout = limit;
        return new MinIOBlobStorage(client, options);
    }

    /// <summary>Содержимое с постоянным зерном: расхождение воспроизводится, а не случается.</summary>
    private static byte[] Bytes(int length)
    {
        var bytes = new byte[length];
        new Random(1315).NextBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// Поддельное хранилище: на HEAD отвечает заголовками объекта, на GET — ими же и телом. Тело
    /// отдаёт кусками с паузой, а дойдя до <c>stallAt</c> — замирает, не закрывая соединения: ровно
    /// так выглядит хранилище, у которого посреди ответа встал диск или сосед по кластеру. Либо, в
    /// режиме обрыва, закрывает соединение на том же месте.
    ///
    /// На всё, кроме HEAD и GET объекта, отвечает 501: появись у клиента запрос другого вида, тест
    /// покраснеет отказом клиента с этим кодом, а не сроком ожидания без слова о причине.
    /// </summary>
    private sealed class FakeStorage : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource _stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<TcpClient> _clients = [];
        private readonly byte[] _body;
        private readonly int _chunk;
        private readonly TimeSpan _pause;
        private readonly int _stallAt;
        private readonly bool _closeAtStall;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public List<string> RequestLines { get; } = [];

        /// <summary>Завершается, когда хранилище отдало свою часть тела и замерло.</summary>
        public Task Stalled => _stalled.Task;

        private FakeStorage(byte[] body, int chunk, TimeSpan pause, int stallAt, bool closeAtStall = false)
        {
            (_body, _chunk, _pause, _stallAt, _closeAtStall) = (body, chunk, pause, stallAt, closeAtStall);
            _listener.Start();
            _ = AcceptLoopAsync();
        }

        /// <summary>Отдаёт заголовки и первые <paramref name="sent"/> байт тела, дальше молчит.</summary>
        public static FakeStorage StallingAfter(byte[] body, int sent) => new(body, sent, TimeSpan.Zero, sent);

        /// <summary>Отдаёт заголовки и первые <paramref name="sent"/> байт тела и закрывает соединение.</summary>
        public static FakeStorage ClosingAfter(byte[] body, int sent) => new(body, sent, TimeSpan.Zero, sent, closeAtStall: true);

        /// <summary>Отдаёт тело целиком, кусками по <paramref name="chunk"/> байт с паузой между ними.</summary>
        public static FakeStorage Dribbling(byte[] body, int chunk, TimeSpan pause) => new(body, chunk, pause, body.Length);

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
                // Соединение клиент держит открытым и шлёт по нему запрос за запросом — HEAD, потом GET.
                while (true)
                {
                    var text = new StringBuilder();
                    while (!text.ToString().Contains("\r\n\r\n"))
                    {
                        var n = await stream.ReadAsync(buffer, _stop.Token);
                        if (n == 0) return;
                        text.Append(Encoding.ASCII.GetString(buffer, 0, n));
                    }
                    var requestLine = text.ToString().Split("\r\n")[0];
                    lock (RequestLines) RequestLines.Add(requestLine);
                    var get = requestLine.StartsWith("GET /bucket/", StringComparison.Ordinal);
                    if (!get && !requestLine.StartsWith("HEAD /bucket/", StringComparison.Ordinal))
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(
                            "HTTP/1.1 501 Not Implemented\r\nContent-Length: 0\r\n\r\n"), _stop.Token);
                        continue;
                    }

                    await stream.WriteAsync(Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\n" +
                        $"Content-Length: {_body.Length}\r\n" +
                        "Content-Type: application/octet-stream\r\n" +
                        "ETag: \"0123456789abcdef0123456789abcdef\"\r\n" +
                        "Last-Modified: Wed, 14 Oct 2026 00:00:00 GMT\r\n" +
                        "Accept-Ranges: bytes\r\n\r\n"), _stop.Token);
                    if (!get) continue;

                    for (var offset = 0; offset < _stallAt; offset += _chunk)
                    {
                        await stream.WriteAsync(_body.AsMemory(offset, Math.Min(_chunk, _stallAt - offset)), _stop.Token);
                        await stream.FlushAsync(_stop.Token);
                        if (_pause > TimeSpan.Zero) await Task.Delay(_pause, _stop.Token);
                    }
                    if (_stallAt >= _body.Length) continue;
                    if (_closeAtStall) { client.Dispose(); return; }

                    _stalled.TrySetResult();
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                }
            }
            catch
            {
                // Тест закончился или клиент оборвал соединение — подделке сообщать об этом некому.
            }
        }

        /// <summary>
        /// Закрывает и соединения, а не только слушателя: синхронное чтение, не услышавшее отмены,
        /// освобождает поток пула лишь тогда, когда хранилище оборвёт связь. Без этого красный тест
        /// оставлял бы за собой занятый поток до конца прогона.
        /// </summary>
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
