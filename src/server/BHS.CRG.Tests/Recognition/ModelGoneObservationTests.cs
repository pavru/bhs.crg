using System.Net;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Http;
using BHS.CRG.Infrastructure.Recognition;
using BHS.CRG.Infrastructure.Settings;
using BHS.CRG.Tests.Support;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace BHS.CRG.Tests.Recognition;

/// <summary>
/// Настоящий 404 от распознавания отмечает модель снятой (issue #923). До этого снятие узнавалось
/// только плановой платной пробой раз в часы, а бесплатный и точный отказ распознавания пропадал.
///
/// Без сети: поставщика изображает обработчик. Имена моделей у тестов свои — выполняющиеся проверки
/// каталога общие на процесс.
/// </summary>
public class ModelGoneObservationTests
{
    private const string GoogleGone =
        "{\"error\":{\"code\":404,\"message\":\"models/gemini-old is not found for API version v1beta. " +
        "Please update your code to use models/gemini-new for the latest features.\",\"status\":\"NOT_FOUND\"}}";

    private sealed class Provider(HttpStatusCode status, string body) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;
        public TaskCompletionSource? Hold { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            if (Hold is { } hold) await hold.Task;
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
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

    private static FakeSettings Settings(string engine, IntegrationEngine cfg)
    {
        var m = new IntegrationSettingsModel { RecognitionOrder = [engine] };
        m.Recognition[engine] = cfg;
        return new FakeSettings(m);
    }

    private static readonly IReadOnlyList<RecognitionField> Fields = [new("Номер", "Номер", "string")];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47];

    private static RecognitionModelCatalog Catalog(HttpMessageHandler? probe = null)
        => new(new SingleClientFactory(new HttpClient(probe ?? new Provider(HttpStatusCode.OK, "{}"))), new MemoryCache(new MemoryCacheOptions()),
            NullLogger<RecognitionModelCatalog>.Instance, []);

    // ── Движки различают «модель снята» ─────────────────────────────────────────

    [Fact]
    public async Task Gemini_404_это_снятая_модель_с_советом_поставщика()
    {
        var cfg = new IntegrationEngine { Enabled = true, ApiKey = "k", Model = "gemini-old" };
        var engine = new GeminiRecognizerEngine(new HttpClient(new Provider(HttpStatusCode.NotFound, GoogleGone)),
            Settings("Gemini", cfg), new OutboundProxyState(), NullLogger<GeminiRecognizerEngine>.Instance);

        var ex = await Assert.ThrowsAsync<RecognitionModelGoneException>(() => engine.RecognizeRawAsync(Png, "image/png", Fields));

        Assert.Equal("Gemini", ex.Engine);
        Assert.Equal("gemini-old", ex.Model);
        Assert.Equal("поставщик рекомендует gemini-new", ex.Advice);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]    // Anthropic с пустым счётом отвечает так и на снятую модель
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Прочие_отказы_не_приговор_модели(HttpStatusCode status)
    {
        var cfg = new IntegrationEngine { Enabled = true, ApiKey = "k", Model = "claude-x" };
        var engine = new AnthropicRecognizerEngine(new HttpClient(new Provider(status, "{\"error\":{}}")),
            Settings("Anthropic", cfg), new OutboundProxyState(), NullLogger<AnthropicRecognizerEngine>.Instance);

        var ex = await Assert.ThrowsAsync<RecognitionUnavailableException>(() => engine.RecognizeRawAsync(Png, "image/png", Fields));
        Assert.IsNotType<RecognitionModelGoneException>(ex);
    }

    [Fact]
    public async Task Anthropic_404_это_снятая_модель()
    {
        var cfg = new IntegrationEngine { Enabled = true, ApiKey = "k", Model = "claude-old" };
        var engine = new AnthropicRecognizerEngine(
            new HttpClient(new Provider(HttpStatusCode.NotFound, "{\"type\":\"error\",\"error\":{\"type\":\"not_found_error\",\"message\":\"model: claude-old\"}}")),
            Settings("Anthropic", cfg), new OutboundProxyState(), NullLogger<AnthropicRecognizerEngine>.Instance);

        var ex = await Assert.ThrowsAsync<RecognitionModelGoneException>(() => engine.RecognizeRawAsync(Png, "image/png", Fields));
        Assert.Equal("claude-old", ex.Model);
    }

    // ── Цепочка передаёт наблюдение каталогу ─────────────────────────────────────

    [Fact]
    public async Task Отказ_распознавания_виден_каталогу_без_платной_пробы()
    {
        var cfg = new IntegrationEngine { Enabled = true, ApiKey = "k", Model = "gemini-chain-old" };
        var settings = Settings("Gemini", cfg);
        var probe = new Provider(HttpStatusCode.OK, "{}");
        var catalog = Catalog(probe);
        var engine = new GeminiRecognizerEngine(new HttpClient(new Provider(HttpStatusCode.NotFound, GoogleGone)),
            settings, new OutboundProxyState(), NullLogger<GeminiRecognizerEngine>.Instance);
        var selector = new RecognitionEngineSelector([engine], settings, catalog, NullLogger<RecognitionEngineSelector>.Instance);
        var chain = new ChainDocumentRecognizer(selector, NullLogger<ChainDocumentRecognizer>.Instance);

        await Assert.ThrowsAsync<RecognitionModelGoneException>(() => chain.RecognizeAsync(Png, "image/png", Fields));

        // CacheOnly — так спрашивает мониторинг на кругах без пробы: вердикт уже там, и за него не платили.
        var status = await catalog.GetStatusAsync("Gemini", cfg, "gemini-chain-old", ModelProbe.CacheOnly);
        Assert.Equal(ModelState.Gone, status.State);
        Assert.Equal("поставщик рекомендует gemini-new", status.Advice);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task Модель_сменённую_за_время_запроса_не_приговаривает()
    {
        // Отказала прежняя модель, а в настройках уже новая: запись под новой объявила бы снятой то,
        // что никто не проверял.
        var cfg = new IntegrationEngine { Enabled = true, ApiKey = "k", Model = "gemini-switched-new" };
        var catalog = Catalog();
        var selector = new RecognitionEngineSelector([], Settings("Gemini", cfg), catalog, NullLogger<RecognitionEngineSelector>.Instance);

        await selector.ObserveGoneAsync(new RecognitionModelGoneException("Gemini", "gemini-switched-old", null, "снята"));

        Assert.Equal(ModelState.Unknown, (await catalog.GetStatusAsync("Gemini", cfg, "gemini-switched-new", ModelProbe.CacheOnly)).State);
        Assert.Equal(ModelState.Unknown, (await catalog.GetStatusAsync("Gemini", cfg, "gemini-switched-old", ModelProbe.CacheOnly)).State);
    }

    // ── Каталог ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Ollama", "k")]    // у Ollama «снята» — это «не скачана», за это отвечает список установленных
    [InlineData("Gemini", "")]     // без ключа вердикт не прочтёт никто: ключ входит в ключ кэша
    public void Наблюдение_без_читателя_не_записывается(string engine, string apiKey)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var catalog = new RecognitionModelCatalog(new SingleClientFactory(new HttpClient()), cache, NullLogger<RecognitionModelCatalog>.Instance, []);

        catalog.ObserveGone(engine, new IntegrationEngine { Enabled = true, ApiKey = apiKey, Model = "m" }, "m", null);

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task Проба_без_ответа_не_затирает_наблюдённый_вердикт()
    {
        // Проба ушла до отказа распознавания и вернулась ни с чем (сеть, лимит): её «не проверено»
        // не должно стереть точное «снята», иначе мониторинг объявил бы модель восстановленной.
        var probe = new Provider(HttpStatusCode.ServiceUnavailable, "{}")
            { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var catalog = Catalog(probe);
        var cfg = new IntegrationEngine { Enabled = true, ApiKey = "k", Model = "gemini-race" };

        var pending = catalog.GetStatusAsync("Gemini", cfg, "gemini-race", ModelProbe.IfUnknown);
        await Task.Delay(100);
        catalog.ObserveGone("Gemini", cfg, "gemini-race", "поставщик рекомендует gemini-new");
        probe.Hold.SetResult();

        Assert.Equal(ModelState.Gone, (await pending).State);
        Assert.Equal(ModelState.Gone, (await catalog.GetStatusAsync("Gemini", cfg, "gemini-race", ModelProbe.CacheOnly)).State);
    }
}
