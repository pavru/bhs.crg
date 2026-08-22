using System.Net;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace BHS.CRG.Tests.Recognition;

/// <summary>
/// Полный отказ веб-поиска не притворяется пустой выдачей (issue #797).
///
/// Движки возвращали пустой список и когда не нашли ничего, и когда не смогли спросить. Разница для
/// пользователя решающая: «по запросу ничего нет» — повод поменять запрос, «спросить было негде» —
/// повод чинить настройки. Пока оба случая выглядят одинаково, второй не диагностируется вовсе.
/// </summary>
public class SearchOutageTests
{
    private sealed class Handler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond());
    }

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class FakeSettings(IntegrationSettingsModel model) : IIntegrationSettings
    {
        public Task<IntegrationSettingsModel> GetEffectiveAsync(CancellationToken ct = default) => Task.FromResult(model);
        public Task SaveAsync(IntegrationSettingsModel update, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveSmtpAsync(SmtpSettings smtp, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveUpdatesAsync(UpdateCheckSettings u, CancellationToken ct = default) => Task.CompletedTask;
        public void Invalidate() { }
    }

    private static FakeSettings WebSettings() => new(new IntegrationSettingsModel
    {
        WebSearch = { ["Serper"] = new IntegrationEngine { Enabled = true, ApiKey = "k" } },
    });

    /// <summary>Движок с предопределённым поведением — подменяет сетевой слой целиком.</summary>
    private sealed class StubEngine(string name, Func<IReadOnlyList<WebHit>> answer) : IWebSearchEngine
    {
        public int Calls { get; private set; }
        public string Name => name;
        public Task<IReadOnlyList<WebHit>> QueryAsync(string query, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(answer());
        }
    }

    private static TieredWebSearch Tiered(params IWebSearchEngine[] engines)
    {
        var model = new IntegrationSettingsModel();
        foreach (var e in engines)
            model.WebSearch[e.Name] = e.Name == "Yandex"
                ? new IntegrationEngine { Enabled = true, ApiKey = "k", FolderId = "f" }
                : new IntegrationEngine { Enabled = true, ApiKey = "k" };
        return new TieredWebSearch(engines, new FakeSettings(model),
            new HttpClient(new Handler(() => new HttpResponseMessage(HttpStatusCode.NotFound))));
    }

    // ── Движок отличает отказ от пустой выдачи ──────────────────────────────────

    [Fact]
    public async Task Serper_HttpError_IsReportedAsOutage_NotEmptyResult()
    {
        var engine = new SerperEngine(
            new HttpClient(new Handler(() => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            { Content = new StringContent("boom") })),
            WebSettings(), NullLogger<SerperEngine>.Instance);

        await Assert.ThrowsAsync<SearchUnavailableException>(() => engine.QueryAsync("кабель"));
    }

    [Fact]
    public async Task Serper_EmptyResults_StayEmpty_NotAnOutage()
    {
        // Пустая выдача — законный ответ, и объявлять её отказом нельзя: пользователь получил бы
        // «поиск недоступен» там, где поиск отработал.
        var engine = new SerperEngine(new HttpClient(new Handler(() => Ok("{\"organic\":[]}"))),
            WebSettings(), NullLogger<SerperEngine>.Instance);

        Assert.Empty(await engine.QueryAsync("такого товара нет"));
    }

    // ── Оркестратор: отказ ВСЕХ ≠ пустая выдача ────────────────────────────────

    [Fact]
    public async Task AllEnginesFail_ReportsOutage()
    {
        var a = new StubEngine("Serper", () => throw new SearchUnavailableException("Serper: не ответил за 30 с."));
        var b = new StubEngine("Yandex", () => throw new SearchUnavailableException("Яндекс ответил 503."));

        var ex = await Assert.ThrowsAsync<SearchUnavailableException>(() => Tiered(a, b).SearchAsync("кабель ВВГнг"));
        Assert.Contains("Ни один движок", ex.Message);
    }

    [Fact]
    public async Task OneEngineSurvives_SearchStillWorks()
    {
        // Отказ одного движка поиск не роняет — ради этого выдачу и агрегируют.
        var dead = new StubEngine("Serper", () => throw new SearchUnavailableException("Serper: не ответил за 30 с."));
        var alive = new StubEngine("Yandex", () => [new WebHit("Сертификат", "https://example.org/cert.pdf", "")]);

        var found = await Tiered(dead, alive).SearchAsync("кабель ВВГнг");
        Assert.Contains(found, c => c.Url.EndsWith("cert.pdf", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AllEnginesAnswerNothing_IsEmptyResult_NotOutage()
    {
        // Все ответили и ничего не нашли — это пустая выдача, а не отказ.
        var a = new StubEngine("Serper", () => []);
        var b = new StubEngine("Yandex", () => []);

        Assert.Empty(await Tiered(a, b).SearchAsync("такого товара нет"));
    }
}
