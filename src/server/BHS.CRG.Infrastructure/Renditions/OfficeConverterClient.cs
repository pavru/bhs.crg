using System.Net;
using System.Net.Http.Headers;
using BHS.CRG.Infrastructure.Http;
using BHS.CRG.Infrastructure.OfficeConversion;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Renditions;

/// <summary>Ответ конвертера: PDF или отказ — ровно одно из двух.</summary>
public readonly record struct ConverterReply(byte[]? Pdf, Rendition.Refused? Refusal);

/// <summary>
/// Вызов конвертера офисных файлов (issue #1268): один файл туда, PDF или названный отказ обратно.
///
/// <para><b>Конвертеру отдаётся только файл, вид которого уже определён по содержимому.</b> Сам он
/// вид не проверяет: текстовый файл с расширением <c>.xlsx</c> возвращается с кодом 200 и PDF, в
/// котором этот текст напечатан. Поэтому и имя файла в запросе наше, по виду, а не то, с которым
/// файл пришёл.</para>
///
/// <para><b>По одному файлу за раз.</b> Сервис пускает новый процесс LibreOffice на каждый документ,
/// и второй запрос встаёт за первым в очередь — а срок у него при этом идёт. Очередь держим у себя:
/// ждущий получает «занят», а не отказ по времени, в котором его файл не виноват.</para>
///
/// <para><b>Текст ответа конвертера человеку не отдаётся и в журнал не пишется</b> — в нём может
/// быть содержимое файла. Из ответа берётся код, а из текста — единственный признак: пароль.</para>
/// </summary>
public sealed class OfficeConverterClient(
    IHttpClientFactory clients, OfficeConverterOptions options, ILogger<OfficeConverterClient> log)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Сколько ждать очереди. Меняется только тестом.</summary>
    public TimeSpan GateWait { get; init; } = RenditionLimits.GateWait;

    /// <summary>Срок вызова целиком, вместе с чтением ответа. Меняется только тестом.</summary>
    public TimeSpan Timeout { get; init; } = OfficeConverterOptions.ClientTimeout;

    /// <param name="file">Офисный файл, уже прошедший бюджет.</param>
    /// <param name="format">Его вид по содержимому.</param>
    public async Task<ConverterReply> ConvertAsync(byte[] file, OfficeFormat format, CancellationToken ct)
    {
        if (!options.Configured)
            return NotSetUp("у этого экземпляра он не настроен");
        if (!await _gate.WaitAsync(GateWait, ct))
            return Unavailable("он занят другими файлами");
        try
        {
            return await SendAsync(file, format, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Чем строятся образы — «gotenberg 8.37.0» (issue #1269); <c>null</c> — сервис себя не назвал.
    ///
    /// <para>Спрашивается у самого сервиса, а не берётся из настройки: настройка знает адрес, а
    /// что по нему стоит, знает только он. Спрашивается при каждом построении и не запоминается —
    /// контейнер меняют, не перезапуская приложение, а отметка на образе обязана говорить о том
    /// конвертере, который его построил.</para>
    ///
    /// <para>Любой отказ здесь — «не знаем», а не отказ построения: образ уже готов.</para>
    /// </summary>
    public async Task<string?> MarkAsync(CancellationToken ct)
    {
        if (!options.Configured) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(MarkBudget);
        using var http = clients.CreateClient(OfficeConverterOptions.ClientName);
        http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        try
        {
            using var response = await http.GetAsync(options.At("version"), HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode != HttpStatusCode.OK) return null;
            // Ответ читается с пределом и сверяется с видом номера версии: по адресу может стоять
            // что угодно, а отметка ложится в базу и показывается человеку.
            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
            var buffer = new byte[MarkMaxLength + 1];
            var read = await body.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, deadline.Token);
            var version = System.Text.Encoding.ASCII.GetString(buffer, 0, read).Trim();
            return read <= MarkMaxLength && LooksLikeVersion(version) ? "gotenberg " + version : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return null;
        }
    }

    private static readonly TimeSpan MarkBudget = TimeSpan.FromSeconds(5);
    private const int MarkMaxLength = 40;

    private static bool LooksLikeVersion(string text) =>
        text.Length > 0 && char.IsAsciiDigit(text[0])
        && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+');

    private async Task<ConverterReply> SendAsync(byte[] file, OfficeFormat format, CancellationToken ct)
    {
        // Срок свой, а не клиента: тот действует только до заголовков ответа, а тело мы читаем сами.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Timeout);
        using var http = clients.CreateClient(OfficeConverterOptions.ClientName);
        http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;

        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(file);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        // По расширению конвертер выбирает, чем файл открывать.
        form.Add(part, "files", "file." + format.ToString().ToLowerInvariant());

        try
        {
            // Только до заголовков: иначе клиент сам сложил бы весь ответ в память раньше, чем мы
            // успели бы спросить о его размере, — и предел ниже проверял бы уже лежащий там PDF.
            using var request = new HttpRequestMessage(HttpMethod.Post, options.At("forms/libreoffice/convert")) { Content = form };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.OK)
                return await ReadPdfAsync(response, deadline.Token);

            log.LogWarning("Конвертер офисных файлов ответил {Status}", (int)response.StatusCode);
            return response.StatusCode switch
            {
                HttpStatusCode.BadRequest when await MentionsPasswordAsync(response, deadline.Token) =>
                    Refused(RenditionRefusal.Protected,
                        "Файл защищён паролем — привести его к читаемому виду нельзя. Снимите пароль и приложите файл заново."),
                // Сам конвертер сказал, что не справился: файл дошёл, LibreOffice его не взял.
                HttpStatusCode.BadRequest or HttpStatusCode.InternalServerError =>
                    Refused(RenditionRefusal.Failed, "Привести файл к читаемому виду не удалось: конвертер его не открыл."),
                // Срок самого сервиса: очереди перед ним нет, значит время ушло на этот файл.
                HttpStatusCode.ServiceUnavailable =>
                    Refused(RenditionRefusal.TooLarge, "Файл слишком велик для читаемого вида: конвертер не успел его обработать."),
                // По адресу стоит не конвертер или не тот его путь: повтор тут ничего не изменит.
                HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    or HttpStatusCode.MethodNotAllowed =>
                    NotSetUp($"по настроенному адресу он не отвечает как конвертер (код {(int)response.StatusCode})"),
                _ => Unavailable($"он ответил {(int)response.StatusCode}"),
            };
        }
        catch (OperationCanceledException ex) when (HttpFailure.IsTimeout(ex, ct))
        {
            log.LogWarning("Конвертер офисных файлов не ответил за {Timeout}", Timeout);
            return Unavailable($"он не ответил за {HttpFailure.Format(Timeout)}");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            log.LogWarning("Конвертер офисных файлов недоступен: {Error}", ex.Message);
            return Unavailable("он не отвечает");
        }
    }

    /// <summary>Тело читаем сами и с пределом: размер PDF конвертер не ограничивает ничем.</summary>
    private static async Task<ConverterReply> ReadPdfAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var pdf = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(buffer, ct)) > 0)
        {
            if (pdf.Length + read > RenditionLimits.MaxPdfBytes)
                return Refused(RenditionRefusal.TooLarge,
                    $"Файл слишком велик для читаемого вида: PDF из него больше {RenditionLimits.MaxPdfBytes / (1024 * 1024)} МБ.");
            pdf.Write(buffer, 0, read);
        }
        return new(pdf.ToArray(), null);
    }

    private static async Task<bool> MentionsPasswordAsync(HttpResponseMessage response, CancellationToken ct) =>
        (await response.Content.ReadAsStringAsync(ct)).Contains("password", StringComparison.OrdinalIgnoreCase);

    private static ConverterReply Refused(RenditionRefusal kind, string reason) => new(null, new(kind, reason));

    private static ConverterReply NotSetUp(string why) =>
        Refused(RenditionRefusal.NotSetUp,
            $"Офисные файлы здесь к читаемому виду не приводятся: конвертер недоступен — {why}. Обратитесь к администратору.");

    private static ConverterReply Unavailable(string why) =>
        Refused(RenditionRefusal.Unavailable, $"Конвертер офисных файлов недоступен: {why}. Повторите позже.");
}
