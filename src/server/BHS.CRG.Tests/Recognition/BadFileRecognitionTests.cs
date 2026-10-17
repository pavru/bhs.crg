using System.Net;
using System.Text;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Http;
using BHS.CRG.Infrastructure.Recognition;
using BHS.CRG.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharpCore.Pdf;

namespace BHS.CRG.Tests.Recognition;

/// <summary>
/// Негодный файл называется отказом о ФАЙЛЕ, а не «распознавание недоступно» (issue #1343).
///
/// <para>Раньше битый PDF уходил к облачному движку, тот отвечал 400, и человек видел «Gemini ответил
/// 400» — в форме счёта, в фоновых задачах и в колокольчике. Выглядело это как сбой сервиса, а запрос
/// при этом был платным. Без сети: поставщика изображает обработчик, который считает обращения.</para>
/// </summary>
public class BadFileRecognitionTests
{
    /// <summary>Так выглядят файлы живых прогонов (#1344): начало как у PDF, внутри строка текста.</summary>
    private static readonly byte[] FakePdf = Encoding.UTF8.GetBytes("%PDF-1.4 не настоящий");

    private static readonly IReadOnlyList<RecognitionField> Fields = [new("Номер", "Номер", "string")];

    private const string GoogleBadDocument =
        "{\"error\":{\"code\":400,\"message\":\"Request contains an invalid argument.\",\"status\":\"INVALID_ARGUMENT\"}}";

    private const string GoogleBadKey =
        "{\"error\":{\"code\":400,\"message\":\"API key not valid. Please pass a valid API key.\",\"status\":\"INVALID_ARGUMENT\"," +
        "\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.ErrorInfo\",\"reason\":\"API_KEY_INVALID\",\"domain\":\"googleapis.com\"}]}}";

    private sealed class Provider(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    /// <summary>Движок, который считает вызовы и отвечает заданным: текстом или отказом.</summary>
    private sealed class Counting(string name, Exception? refusal = null) : IRecognizerEngine
    {
        public int Calls;
        public string Name => name;
        public bool Accepts(string mimeType) => true;

        public Task<string> RecognizeRawAsync(byte[] file, string mimeType, IReadOnlyList<RecognitionField> fields,
            Func<IReadOnlyList<RecognitionField>, string>? promptBuilder = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return refusal is null ? Task.FromResult("{\"Номер\":\"17\"}") : Task.FromException<string>(refusal);
        }
    }

    private sealed class FakeSettings(IntegrationSettingsModel model) : IIntegrationSettings
    {
        public Task<IntegrationSettingsModel> GetEffectiveAsync(CancellationToken ct = default) => Task.FromResult(model);
        public Task SaveAsync(IntegrationSettingsModel update, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveSmtpAsync(SmtpSettings smtp, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveUpdatesAsync(UpdateCheckSettings u, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveBackupScheduleAsync(BackupScheduleSettings b, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveGithubAsync(GithubSettings g, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveProxyAsync(ProxySettings p, CancellationToken ct = default) => Task.CompletedTask;
        public void Invalidate() { }
    }

    /// <summary>Настройки, где включены и настроены названные движки, в этом порядке.</summary>
    private static FakeSettings Settings(params string[] engines)
    {
        var m = new IntegrationSettingsModel { RecognitionOrder = [.. engines] };
        foreach (var e in engines)
            m.Recognition[e] = new IntegrationEngine { Enabled = true, ApiKey = "k", Model = $"{e.ToLowerInvariant()}-bad-file" };
        return new FakeSettings(m);
    }

    /// <summary>Каталог пуст — про зрение ничего не известно, движки идут как прежде (см. EngineTimeoutTests).</summary>
    private static ChainDocumentRecognizer Chain(FakeSettings settings, params IRecognizerEngine[] engines)
        => new(new RecognitionEngineSelector(engines, settings, new FakeCatalog(), NullLogger<RecognitionEngineSelector>.Instance),
            NullLogger<ChainDocumentRecognizer>.Instance);

    private static GeminiRecognizerEngine Gemini(Provider provider) => new(
        new HttpClient(provider), Settings("Gemini"), new OutboundProxyState(), NullLogger<GeminiRecognizerEngine>.Instance);

    private static byte[] RealPdf()
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    /// <summary>
    /// Сторож задачи: битый PDF до движка не доходит. Ломается, если убрать проверку файла из цепочки —
    /// тогда движок вызван, и отказ — его словами.
    /// </summary>
    [Fact]
    public async Task Битый_PDF_не_уходит_к_движку()
    {
        var engine = new Counting("Gemini");
        var chain = Chain(Settings("Gemini"), engine);

        var refusal = await Assert.ThrowsAsync<RecognitionFileRejectedException>(
            () => chain.RecognizeAsync(FakePdf, "application/pdf", Fields));

        Assert.Equal(0, engine.Calls);
        Assert.Contains("повреждён", refusal.Message);
    }

    [Fact]
    public async Task Пустой_файл_не_уходит_к_движку()
    {
        var engine = new Counting("Gemini");

        await Assert.ThrowsAsync<RecognitionFileRejectedException>(
            () => Chain(Settings("Gemini"), engine).RecognizeAsync([], "image/png", Fields));

        Assert.Equal(0, engine.Calls);
    }

    /// <summary>Проверка не мешает исправному файлу — иначе она отказала бы всем.</summary>
    [Fact]
    public async Task Исправный_PDF_доходит_до_движка()
    {
        var engine = new Counting("Gemini");

        var result = await Chain(Settings("Gemini"), engine).RecognizeAsync(RealPdf(), "application/pdf", Fields);

        Assert.Equal(1, engine.Calls);
        Assert.Equal("17", result.Values["Номер"]);
    }

    /// <summary>
    /// Отказ движка о файле прекращает перебор: следующий движок получил бы тот же файл. Ломается, если
    /// цепочка ловит отказ о файле как обычную недоступность.
    /// </summary>
    [Fact]
    public async Task Отказ_о_файле_не_уходит_к_следующему_движку()
    {
        var first = new Counting("Gemini", new RecognitionFileRejectedException("Gemini отклонил документ как негодный."));
        var second = new Counting("Anthropic");

        await Assert.ThrowsAsync<RecognitionFileRejectedException>(
            () => Chain(Settings("Gemini", "Anthropic"), first, second).RecognizeAsync(RealPdf(), "application/pdf", Fields));

        Assert.Equal(1, first.Calls);
        Assert.Equal(0, second.Calls);
    }

    /// <summary>Gemini: 400 INVALID_ARGUMENT без названной причины — это документ, и повтора нет.</summary>
    [Fact]
    public async Task Gemini_400_о_документе_это_отказ_о_файле()
    {
        var provider = new Provider(HttpStatusCode.BadRequest, GoogleBadDocument);

        var refusal = await Assert.ThrowsAsync<RecognitionFileRejectedException>(
            () => Gemini(provider).RecognizeRawAsync(RealPdf(), "application/pdf", Fields));

        Assert.Equal(1, provider.Calls);
        Assert.Contains("негодный", refusal.Message);
        // Сообщение поставщика — свободный текст: в отказ оно не идёт (issue #1050).
        Assert.DoesNotContain("invalid argument", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Неверный ключ отвечает тем же INVALID_ARGUMENT — но причина названа, и это отказ сервиса, а не
    /// файла. Ломается, если отличать документ только по коду ответа.
    /// </summary>
    [Fact]
    public async Task Gemini_400_о_ключе_это_отказ_сервиса_с_кодом()
    {
        var refusal = await Assert.ThrowsAsync<RecognitionUnavailableException>(
            () => Gemini(new Provider(HttpStatusCode.BadRequest, GoogleBadKey)).RecognizeRawAsync(RealPdf(), "application/pdf", Fields));

        Assert.IsNotType<RecognitionFileRejectedException>(refusal);
        Assert.Equal("Gemini ответил 400 (INVALID_ARGUMENT).", refusal.Message);
        Assert.DoesNotContain("API key", refusal.Message);
    }

    [Fact]
    public async Task Anthropic_400_называет_код_но_не_сообщение()
    {
        var engine = new AnthropicRecognizerEngine(
            new HttpClient(new Provider(HttpStatusCode.BadRequest,
                "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"Your credit balance is too low\"}}")),
            Settings("Anthropic"), new OutboundProxyState(), NullLogger<AnthropicRecognizerEngine>.Instance);

        var refusal = await Assert.ThrowsAsync<RecognitionUnavailableException>(
            () => engine.RecognizeRawAsync(RealPdf(), "application/pdf", Fields));

        Assert.Equal("Anthropic ответил 400 (invalid_request_error).", refusal.Message);
    }

    /// <summary>Код берётся, только если выглядит кодом: свободный текст в этом поле — не код.</summary>
    [Theory]
    [InlineData("{\"error\":{\"status\":\"PERMISSION_DENIED\"}}", "PERMISSION_DENIED")]
    [InlineData("{\"error\":{\"status\":\"key AIza123 is bad\"}}", null)]
    [InlineData("{\"error\":{\"status\":42}}", null)]
    [InlineData("<html>502</html>", null)]
    [InlineData("{\"error\":\"нет\"}", null)]
    public void Код_ошибки_только_словом_из_словаря(string body, string? code)
        => Assert.Equal(code, ProviderErrorCode.Of(body, "error", "status"));
}
