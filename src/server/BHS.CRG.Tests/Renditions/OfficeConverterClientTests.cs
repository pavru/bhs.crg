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

    /// <summary>
    /// Отметка «чем построен образ» ложится в базу и показывается человеку, а по адресу конвертера
    /// может стоять что угодно. Всё, что не похоже на номер версии, — «неизвестно», а не отметка.
    /// </summary>
    [Theory]
    [InlineData("8.37.0", "gotenberg 8.37.0")]
    [InlineData("8.37.0\n", "gotenberg 8.37.0")]
    [InlineData("8.37.0-libreoffice", "gotenberg 8.37.0-libreoffice")]
    [InlineData("<html>nginx</html>", null)]
    [InlineData("", null)]
    [InlineData("8.37.0 and a very long tail that no version number would ever carry", null)]
    public async Task Отметка_конвертера_это_его_номер_версии_или_ничего(string body, string? mark)
    {
        var client = Client(MustNotBeCalled, version: body);

        Assert.Equal(mark, await client.MarkAsync(CancellationToken.None));
    }

    /// <summary>
    /// Отметка необязательна, и готовый образ её не ждёт: вопрос о версии уходит одновременно с
    /// преобразованием. Здесь преобразование не кончится, пока о версии не спросили, — задай служба
    /// вопрос после него, тест не дождался бы.
    /// </summary>
    [Fact]
    public async Task Версию_спрашивают_одновременно_с_преобразованием()
    {
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Client(
            async (_, ct) =>
            {
                await asked.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                return PdfReply(Pdf("invoice supplier total"));
            },
            onVersion: (_, _) =>
            {
                asked.TrySetResult();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("8.37.0") });
            });
        var builder = new OfficeRenditionBuilder(client, Microsoft.Extensions.Logging.Abstractions.NullLogger<OfficeRenditionBuilder>.Instance);

        var built = Assert.IsType<Rendition.Built>(
            await builder.BuildAsync(Workbook("invoice", "supplier", "total"), OfficeFormat.Xlsx, CancellationToken.None));

        Assert.Equal("gotenberg 8.37.0", built.Converter);
    }

    /// <summary>Сервис о версии молчит — образ всё равно есть, и ждали его недолго.</summary>
    [Fact]
    public async Task Молчание_о_версии_образ_не_отменяет()
    {
        var client = Client(
            Returns(Pdf("invoice supplier total")), markWait: TimeSpan.FromMilliseconds(100),
            onVersion: async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException();
            });
        var builder = new OfficeRenditionBuilder(client, Microsoft.Extensions.Logging.Abstractions.NullLogger<OfficeRenditionBuilder>.Instance);

        var built = Assert.IsType<Rendition.Built>(
            await builder.BuildAsync(Workbook("invoice", "supplier", "total"), OfficeFormat.Xlsx, CancellationToken.None));

        Assert.Null(built.Converter);
    }

    /// <summary>
    /// Вид отказа лежит в таблице образов ИМЕНЕМ. Переименовали или убрали член — старые записи
    /// перестали читаться. Список меняют вместе с миграцией данных, а не одной правкой перечня.
    /// </summary>
    [Fact]
    public void Имена_видов_отказа_это_формат_хранения()
    {
        Assert.Equal(
            ["Protected", "WrongFormat", "Corrupted", "Empty", "TooLarge", "Failed", "Unavailable", "NotSetUp"],
            Enum.GetNames<RenditionRefusal>());
    }

    [Fact]
    public async Task Конвертер_не_назвал_себя_отметки_нет_а_не_отказ()
    {
        Assert.Null(await Client(MustNotBeCalled, version: null).MarkAsync(CancellationToken.None));
        Assert.Null(await Client(MustNotBeCalled, baseUrl: null, version: "8.37.0").MarkAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task Без_адреса_конвертера_нет_и_повтор_этого_не_лечит(string? baseUrl)
    {
        var refused = await RefusalOf(Client(MustNotBeCalled, baseUrl));

        // Не «недоступен»: тот зовёт повторить, а здесь повторять можно без конца (ревью PR #1280).
        Assert.Equal(RenditionRefusal.NotSetUp, refused.Kind);
        Assert.False(refused.RetryHelps);
        Assert.Contains("не настроен", refused.Reason);
        Assert.DoesNotContain("Повторите", refused.Reason);
    }

    /// <summary>
    /// Размер PDF конвертер не ограничивает ничем, и ответ читается с пределом — НЕ ПОСЛЕ того, как
    /// клиент сложил его в память целиком: восемьдесят мегабайт ответа, а прочитано чуть больше
    /// предела.
    /// </summary>
    [Fact]
    public async Task Ответ_больше_предела_обрывается_на_пределе_а_не_читается_целиком()
    {
        var body = new Counted(80L * 1024 * 1024);
        var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }));

        var refused = await RefusalOf(client);

        Assert.Equal(RenditionRefusal.TooLarge, refused.Kind);
        Assert.InRange(body.ReadSoFar, RenditionLimits.MaxPdfBytes, RenditionLimits.MaxPdfBytes + 1024 * 1024);
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
    // По адресу стоит не конвертер: повтор ничего не изменит.
    [InlineData(HttpStatusCode.NotFound, "", RenditionRefusal.NotSetUp)]
    [InlineData(HttpStatusCode.Unauthorized, "", RenditionRefusal.NotSetUp)]
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
