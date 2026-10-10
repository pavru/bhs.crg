using System.Diagnostics;
using System.Net;
using BHS.CRG.Infrastructure.Renditions;
using static BHS.CRG.Tests.Renditions.RenditionFixtures;

namespace BHS.CRG.Tests.Renditions;

/// <summary>
/// Вызов конвертера офисных файлов (issue #1268): что его ответ значит для человека.
///
/// <para>Повтором лечится один отказ — «конвертер недоступен». Перепутать его с отказом по файлу
/// можно в обе стороны, и обе плохи: человек либо бесконечно повторяет загрузку файла, который не
/// откроется никогда, либо бросает годный файл из-за перезапуска сервиса.</para>
/// </summary>
public class OfficeConverterClientTests
{
    private static readonly byte[] File = [1, 2, 3];

    private static async Task<Rendition.Refused> RefusalOf(OfficeConverterClient client)
    {
        var reply = await client.ConvertAsync(File, OfficeFormat.Xlsx, CancellationToken.None);
        Assert.Null(reply.Pdf);
        return Assert.IsType<Rendition.Refused>(reply.Refusal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task Без_адреса_конвертер_недоступен_и_никого_не_зовут(string? baseUrl)
    {
        var refused = await RefusalOf(Client(MustNotBeCalled, baseUrl));

        Assert.Equal(RenditionRefusal.Unavailable, refused.Kind);
        Assert.True(refused.RetryHelps);
        Assert.Contains("не настроен", refused.Reason);
    }

    [Fact]
    public async Task Ответ_200_это_PDF()
    {
        var reply = await Client(Returns([7, 7, 7])).ConvertAsync(File, OfficeFormat.Docx, CancellationToken.None);

        Assert.Null(reply.Refusal);
        Assert.Equal([7, 7, 7], reply.Pdf);
    }

    /// <summary>Тексты ответов — настоящие, сняты с сервиса той версии, что стоит в поставке.</summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest,
        "The document 'file.xlsx' is password-protected. Provide its password in the 'password' form field.",
        RenditionRefusal.Protected)]
    [InlineData(HttpStatusCode.BadRequest, "Invalid form data", RenditionRefusal.Failed)]
    [InlineData(HttpStatusCode.InternalServerError,
        "LibreOffice failed to convert the document 'file.xlsx'. The request is valid and may be retried.",
        RenditionRefusal.Failed)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Service Unavailable", RenditionRefusal.TooLarge)]
    [InlineData(HttpStatusCode.BadGateway, "", RenditionRefusal.Unavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout, "", RenditionRefusal.Unavailable)]
    [InlineData(HttpStatusCode.NotFound, "", RenditionRefusal.Unavailable)]
    public async Task Отказ_сервиса_назван_своим_видом(HttpStatusCode status, string body, RenditionRefusal expected)
    {
        var refused = await RefusalOf(Client(Answers(status, body)));

        Assert.Equal(expected, refused.Kind);
        Assert.Equal(expected == RenditionRefusal.Unavailable, refused.RetryHelps);
    }

    /// <summary>
    /// В ответе конвертера бывает содержимое файла и всегда — его внутренности. Человеку уходит
    /// текст службы, и ни слова оттуда.
    /// </summary>
    [Fact]
    public async Task Текст_ответа_конвертера_человеку_не_отдаётся()
    {
        var refused = await RefusalOf(Client(Answers(HttpStatusCode.InternalServerError, "SECRET-CELL-VALUE soffice.bin crashed")));

        Assert.DoesNotContain("SECRET", refused.Reason);
        Assert.DoesNotContain("soffice", refused.Reason);
    }

    [Fact]
    public async Task Сервис_до_которого_не_дозвались_недоступен()
    {
        var refused = await RefusalOf(Client((_, _) => throw new HttpRequestException("Connection refused (converter:3000)")));

        Assert.Equal(RenditionRefusal.Unavailable, refused.Kind);
        Assert.DoesNotContain("converter:3000", refused.Reason);
    }

    /// <summary>
    /// Зависший конвертер: соединение принято, ответа нет. Отказ обязан прийти В СРОК — загрузка
    /// файла счёта ждёт образа, и без срока она ждала бы вечно.
    /// </summary>
    [Fact]
    public async Task Зависший_сервис_получает_отказ_в_срок()
    {
        var hung = Client(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new UnreachableException();
        }, timeout: TimeSpan.FromMilliseconds(200));

        var clock = Stopwatch.StartNew();
        var refused = await RefusalOf(hung);

        Assert.Equal(RenditionRefusal.Unavailable, refused.Kind);
        Assert.Contains("не ответил", refused.Reason);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"отказ шёл {clock.Elapsed}");
    }

    /// <summary>Срок действует и на чтение ответа: заголовки пришли, а тело остановилось на полпути.</summary>
    [Fact]
    public async Task Ответ_оборвавшийся_на_теле_получает_отказ_в_срок()
    {
        var stalled = Client((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NeverEnds()) }),
            timeout: TimeSpan.FromMilliseconds(200));

        var refused = await RefusalOf(stalled);

        Assert.Equal(RenditionRefusal.Unavailable, refused.Kind);
    }

    [Fact]
    public async Task Отмена_вызывающим_остаётся_отменой()
    {
        using var stop = new CancellationTokenSource();
        var client = Client(async (_, ct) =>
        {
            stop.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            throw new UnreachableException();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ConvertAsync(File, OfficeFormat.Xlsx, stop.Token));
    }

    /// <summary>
    /// По одному файлу за раз. Второй файл ждёт у нас, а не в очереди сервиса — там у него шёл бы
    /// срок, и отказ по времени получил бы файл, который ни при чём.
    /// </summary>
    [Fact]
    public async Task Второй_файл_ждёт_первого_и_получает_занят_а_не_отказ_по_файлу()
    {
        var release = new TaskCompletionSource();
        var calls = 0;
        var client = Client(async (_, _) =>
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            return PdfReply([1]);
        }, gateWait: TimeSpan.FromMilliseconds(100));

        var first = client.ConvertAsync(File, OfficeFormat.Xlsx, CancellationToken.None);
        var second = await RefusalOf(client);

        Assert.Equal(RenditionRefusal.Unavailable, second.Kind);
        Assert.Contains("занят", second.Reason);
        Assert.Equal(1, calls);

        release.SetResult();
        Assert.NotNull((await first).Pdf);
        // Ворота отпущены: следующий файл проходит.
        Assert.NotNull((await client.ConvertAsync(File, OfficeFormat.Xlsx, CancellationToken.None)).Pdf);
    }

    /// <summary>Поток, который отдал начало и замолчал: чтение ждёт до отмены.</summary>
    private sealed class NeverEnds : Stream
    {
        private bool _started;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_started) { _started = true; buffer.Span[0] = (byte)'%'; return 1; }
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
    }
}
