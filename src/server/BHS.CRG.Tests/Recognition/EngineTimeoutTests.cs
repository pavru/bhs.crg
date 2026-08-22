using System.Net;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Recognition;
using BHS.CRG.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace BHS.CRG.Tests.Recognition;

/// <summary>
/// Таймаут движка — недоступность движка, а не обрыв работы (issue #797).
///
/// Живой случай: Gemini не ответил за свои две минуты, <c>HttpClient</c> бросил
/// <see cref="TaskCanceledException"/>, тот прошёл сквозь фильтр <c>when (ex is not
/// OperationCanceledException)</c>, сквозь цепочку — и пользователь получил 500 «Внутренняя ошибка»
/// вместо перехода к следующему движку.
///
/// Проверяем ОБЕ стороны различения: при живом токене — обёртка в
/// <see cref="RecognitionUnavailableException"/>, при отменённом — отмена по-прежнему пробрасывается
/// (ради чего фильтр и писали).
/// </summary>
public class EngineTimeoutTests
{
    // ── Заглушки ────────────────────────────────────────────────────────────────

    /// <summary>Ведёт себя как HttpClient при истёкшем Timeout: TaskCanceledException, токен вызывающего жив.</summary>
    private sealed class TimingOutHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            ct.ThrowIfCancellationRequested();   // отменённый токен вызывающего — отдаём отмену, как настоящий клиент
            throw new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout of 120 seconds elapsing.",
                new TimeoutException());
        }
    }

    private sealed class JsonHandler(string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private sealed class FakeSettings(IntegrationSettingsModel model) : IIntegrationSettings
    {
        public Task<IntegrationSettingsModel> GetEffectiveAsync(CancellationToken ct = default) => Task.FromResult(model);
        public Task SaveAsync(IntegrationSettingsModel update, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveSmtpAsync(SmtpSettings smtp, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveUpdatesAsync(UpdateCheckSettings u, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveBackupScheduleAsync(BackupScheduleSettings b, CancellationToken ct = default) => Task.CompletedTask;
        public void Invalidate() { }
    }

    private static FakeSettings Settings(string section, string name, IntegrationEngine engine)
    {
        var m = new IntegrationSettingsModel();
        (section == "rec" ? m.Recognition : m.WebSearch)[name] = engine;
        return new FakeSettings(m);
    }

    private static readonly IReadOnlyList<RecognitionField> Fields = [new("Номер", "Номер", "string")];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47];

    private const string GeminiAnswer =
        "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"{\\\"Номер\\\":\\\"7\\\"}\"}]}}]}";

    // ── Распознавание: таймаут ──────────────────────────────────────────────────

    [Fact]
    public async Task Gemini_Timeout_BecomesUnavailable_AndIsNotRetried()
    {
        var handler = new TimingOutHandler();
        var engine = new GeminiRecognizerEngine(new HttpClient(handler),
            Settings("rec", "Gemini", new IntegrationEngine { Enabled = true, ApiKey = "k" }),
            NullLogger<GeminiRecognizerEngine>.Instance);

        // Именно RecognitionTimeoutException, а не базовый: постраничные прогоны отличают
        // «движок не работает» от «страница не уложилась в срок» (см. DataSetPdfRecognitionService).
        var ex = await Assert.ThrowsAsync<RecognitionTimeoutException>(
            () => engine.RecognizeRawAsync(Png, "image/png", Fields));

        Assert.Contains("не ответил за", ex.Message);
        Assert.Contains("120 с", ex.Message);
        // Ретрай таймаута докупал бы только ожидание: три попытки по две минуты на движке,
        // который уже показал, что молчит.
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Anthropic_Timeout_BecomesUnavailable()
    {
        var handler = new TimingOutHandler();
        var engine = new AnthropicRecognizerEngine(new HttpClient(handler),
            Settings("rec", "Anthropic", new IntegrationEngine { Enabled = true, ApiKey = "k" }),
            NullLogger<AnthropicRecognizerEngine>.Instance);

        var ex = await Assert.ThrowsAsync<RecognitionTimeoutException>(
            () => engine.RecognizeRawAsync(Png, "image/png", Fields));
        Assert.Contains("не ответил за", ex.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Ollama_Timeout_BecomesUnavailable_WithItsOwnLongerLimit()
    {
        var handler = new TimingOutHandler();
        var engine = new OllamaRecognizerEngine(new HttpClient(handler),
            Settings("rec", "Ollama", new IntegrationEngine { Enabled = true, Model = "qwen2.5vl:7b" }),
            NullLogger<OllamaRecognizerEngine>.Instance);

        var ex = await Assert.ThrowsAsync<RecognitionTimeoutException>(
            () => engine.RecognizeRawAsync(Png, "image/png", Fields));
        Assert.Contains("не ответил за", ex.Message);
        // Локальная модель считается на этой же машине — срок у неё свой и заведомо больше облачного.
        Assert.Contains("300 с", ex.Message);
        Assert.True(OllamaRecognizerEngine.Timeout > GeminiRecognizerEngine.Timeout);
    }

    // ── Распознавание: отмена пользователем по-прежнему пробрасывается ───────────

    [Theory]
    [InlineData("Gemini")]
    [InlineData("Anthropic")]
    [InlineData("Ollama")]
    public async Task UserCancellation_IsStillPropagated(string name)
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var http = new HttpClient(new TimingOutHandler());
        IRecognizerEngine engine = name switch
        {
            "Gemini" => new GeminiRecognizerEngine(http,
                Settings("rec", "Gemini", new IntegrationEngine { Enabled = true, ApiKey = "k" }),
                NullLogger<GeminiRecognizerEngine>.Instance),
            "Anthropic" => new AnthropicRecognizerEngine(http,
                Settings("rec", "Anthropic", new IntegrationEngine { Enabled = true, ApiKey = "k" }),
                NullLogger<AnthropicRecognizerEngine>.Instance),
            _ => new OllamaRecognizerEngine(http,
                Settings("rec", "Ollama", new IntegrationEngine { Enabled = true, Model = "m" }),
                NullLogger<OllamaRecognizerEngine>.Instance),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.RecognizeRawAsync(Png, "image/png", Fields, ct: cts.Token));
    }

    // ── Веб-поиск: тот же паттерн ───────────────────────────────────────────────

    [Fact]
    public async Task Serper_Timeout_IsReportedAsOutage()
    {
        var engine = new SerperEngine(new HttpClient(new TimingOutHandler()),
            Settings("web", "Serper", new IntegrationEngine { Enabled = true, ApiKey = "k" }),
            NullLogger<SerperEngine>.Instance);

        // Отказ движка назван отказом: оркестратор гасит его до пустой выдачи, но отличает от
        // «ничего не нашлось» — иначе полная недоступность выглядела бы пустым результатом
        // (см. SearchOutageTests). Раньше таймаут летел сквозь Task.WhenAll и ронял весь поиск.
        var ex = await Assert.ThrowsAsync<SearchUnavailableException>(() => engine.QueryAsync("кабель ВВГнг"));
        Assert.Contains("не ответил за", ex.Message);
    }

    [Fact]
    public async Task Yandex_Timeout_IsReportedAsOutage()
    {
        var engine = new YandexEngine(new HttpClient(new TimingOutHandler()),
            Settings("web", "Yandex", new IntegrationEngine { Enabled = true, ApiKey = "k", FolderId = "f" }),
            NullLogger<YandexEngine>.Instance);

        await Assert.ThrowsAsync<SearchUnavailableException>(() => engine.QueryAsync("кабель ВВГнг"));
    }

    [Fact]
    public async Task WebSearch_UserCancellation_IsStillPropagated()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var engine = new SerperEngine(new HttpClient(new TimingOutHandler()),
            Settings("web", "Serper", new IntegrationEngine { Enabled = true, ApiKey = "k" }),
            NullLogger<SerperEngine>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.QueryAsync("кабель", cts.Token));
    }

    // ── Цепочка: таймаут первого движка не обрывает перебор ─────────────────────

    /// <summary>
    /// Цепочка с отбором движков (issue #801). Каталог пуст — то есть про зрение ничего не известно,
    /// и это НАМЕРЕННО главный случай: «не проверено» обязано вести себя ровно как прежде, иначе
    /// канарейка отключила бы работающее распознавание.
    /// </summary>
    private static ChainDocumentRecognizer Chain(FakeSettings settings, IRecognizerEngine[] engines)
        => new(new RecognitionEngineSelector(engines, settings, new FakeCatalog(),
                   NullLogger<RecognitionEngineSelector>.Instance),
               NullLogger<ChainDocumentRecognizer>.Instance);

    private static IntegrationSettingsModel TwoEngines() => new()
    {
        RecognitionOrder = ["Ollama", "Gemini"],
        Recognition =
        {
            ["Ollama"] = new IntegrationEngine { Enabled = true, Model = "m" },
            ["Gemini"] = new IntegrationEngine { Enabled = true, ApiKey = "k" },
        },
    };

    [Fact]
    public async Task Chain_FirstEngineTimesOut_SecondAnswers()
    {
        var timingOut = new TimingOutHandler();
        var answering = new JsonHandler(GeminiAnswer);
        var settings = new FakeSettings(TwoEngines());
        var chain = Chain(settings,
            [
                new OllamaRecognizerEngine(new HttpClient(timingOut), settings, NullLogger<OllamaRecognizerEngine>.Instance),
                new GeminiRecognizerEngine(new HttpClient(answering), settings, NullLogger<GeminiRecognizerEngine>.Instance),
            ]);

        var result = await chain.RecognizeAsync(Png, "image/png", Fields);

        Assert.Equal("7", result.Values["Номер"]);
        Assert.Equal(1, timingOut.Calls);
        Assert.Equal(1, answering.Calls);
    }

    [Fact]
    public async Task Chain_AllEnginesTimeOut_ReportsUnavailable_NotRawCancellation()
    {
        // Эндпоинт отвечает 503 с этим текстом; голое OperationCanceledException доезжало 500-й.
        var settings = new FakeSettings(TwoEngines());
        var chain = Chain(settings,
            [
                new OllamaRecognizerEngine(new HttpClient(new TimingOutHandler()), settings, NullLogger<OllamaRecognizerEngine>.Instance),
                new GeminiRecognizerEngine(new HttpClient(new TimingOutHandler()), settings, NullLogger<GeminiRecognizerEngine>.Instance),
            ]);

        // Цепочка ловит базовый RecognitionUnavailableException — таймаут его наследник, и ни одно
        // место, ловящее базовый тип, о новом знать не обязано.
        var ex = await Assert.ThrowsAnyAsync<RecognitionUnavailableException>(
            () => chain.RecognizeAsync(Png, "image/png", Fields));
        Assert.Contains("не ответил за", ex.Message);
    }

    [Fact]
    public async Task Chain_EnabledButUnconfiguredEngine_IsSkipped()
    {
        // Anthropic «включён», но без ключа — цепочка его не берёт (и пишет об этом в лог; в UI — бейдж).
        var answering = new JsonHandler(GeminiAnswer);
        var untouched = new TimingOutHandler();
        var settings = new FakeSettings(new IntegrationSettingsModel
        {
            RecognitionOrder = ["Anthropic", "Gemini"],
            Recognition =
            {
                ["Anthropic"] = new IntegrationEngine { Enabled = true, ApiKey = "" },
                ["Gemini"] = new IntegrationEngine { Enabled = true, ApiKey = "k" },
            },
        });
        var chain = Chain(settings,
            [
                new AnthropicRecognizerEngine(new HttpClient(untouched), settings, NullLogger<AnthropicRecognizerEngine>.Instance),
                new GeminiRecognizerEngine(new HttpClient(answering), settings, NullLogger<GeminiRecognizerEngine>.Instance),
            ]);

        Assert.Equal("7", (await chain.RecognizeAsync(Png, "image/png", Fields)).Values["Номер"]);
        Assert.Equal(0, untouched.Calls);
    }
}
