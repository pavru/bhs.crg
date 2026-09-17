using System.Net;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Settings;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace BHS.CRG.Tests.Settings;

/// <summary>
/// Режимы пробы и сроки вердиктов каталога моделей (issue #921). Проверяется без сети: поставщика
/// изображает обработчик, который считает запросы, — у настоящего поставщика каждый из них оплачивается.
/// </summary>
public class RecognitionModelCatalogProbeTests
{
    private sealed class Provider : HttpMessageHandler
    {
        public HttpStatusCode Answer { get; set; } = HttpStatusCode.OK;
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var body = Answer == HttpStatusCode.NotFound
                ? "{\"error\":{\"message\":\"models/gemini-x is not found\"}}"
                : "{}";
            return Task.FromResult(new HttpResponseMessage(Answer) { Content = new StringContent(body) });
        }
    }

    private static readonly IntegrationEngine Gemini = new() { Enabled = true, ApiKey = "k", Model = "gemini-x" };

    private static (RecognitionModelCatalog Catalog, Provider Provider) Build()
    {
        var provider = new Provider();
        var catalog = new RecognitionModelCatalog(new HttpClient(provider), new MemoryCache(new MemoryCacheOptions()),
            NullLogger<RecognitionModelCatalog>.Instance, []);
        return (catalog, provider);
    }

    private static Task<ModelStatus> Ask(RecognitionModelCatalog catalog, ModelProbe probe, IntegrationEngine? cfg = null)
        => catalog.GetStatusAsync("Gemini", cfg ?? Gemini, "gemini-x", probe);

    [Fact]
    public async Task Только_кэш_к_поставщику_не_ходит()
    {
        var (catalog, provider) = Build();

        var status = await Ask(catalog, ModelProbe.CacheOnly);

        Assert.Equal(ModelState.Unknown, status.State);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Проба_при_незнании_платится_один_раз()
    {
        var (catalog, provider) = Build();

        await Ask(catalog, ModelProbe.IfUnknown);
        await Ask(catalog, ModelProbe.IfUnknown);
        var cached = await Ask(catalog, ModelProbe.CacheOnly);

        Assert.Equal(1, provider.Calls);
        Assert.Equal(ModelState.Ok, cached.State);
    }

    [Fact]
    public async Task Пересмотр_идёт_к_поставщику_заново()
    {
        var (catalog, provider) = Build();
        await Ask(catalog, ModelProbe.IfUnknown);

        provider.Answer = HttpStatusCode.NotFound;
        var status = await Ask(catalog, ModelProbe.Refresh);

        Assert.Equal(2, provider.Calls);
        Assert.Equal(ModelState.Gone, status.State);
    }

    [Fact]
    public void Вердикт_модель_снята_бессрочен()
    {
        // Иначе через срок кэша «снята» стало бы «не проверено», а проверка, которая на «не проверено»
        // молчит, объявила бы снятую модель восстановленной.
        Assert.Null(RecognitionModelCatalog.StatusTtl(new ModelStatus(ModelState.Gone)));
        Assert.NotNull(RecognitionModelCatalog.StatusTtl(ModelStatus.Ok));
        Assert.NotNull(RecognitionModelCatalog.StatusTtl(ModelStatus.Unknown));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.PaymentRequired)]
    public async Task Пересмотр_без_ответа_не_отменяет_вердикт_модель_снята(HttpStatusCode outage)
    {
        var (catalog, provider) = Build();
        provider.Answer = HttpStatusCode.NotFound;
        await Ask(catalog, ModelProbe.IfUnknown);

        // Лимит, сбой, кончились деньги — это «не проверено», а не «модель вернули».
        provider.Answer = outage;
        var refreshed = await Ask(catalog, ModelProbe.Refresh);
        var cached = await Ask(catalog, ModelProbe.CacheOnly);

        Assert.Equal(ModelState.Gone, refreshed.State);
        Assert.Equal(ModelState.Gone, cached.State);
    }

    [Fact]
    public async Task Пересмотр_с_ответом_снимает_вердикт_модель_снята()
    {
        var (catalog, provider) = Build();
        provider.Answer = HttpStatusCode.NotFound;
        await Ask(catalog, ModelProbe.IfUnknown);

        provider.Answer = HttpStatusCode.OK;
        await Ask(catalog, ModelProbe.Refresh);

        Assert.Equal(ModelState.Ok, (await Ask(catalog, ModelProbe.CacheOnly)).State);
    }

    [Fact]
    public async Task Смена_ключа_это_новый_вопрос()
    {
        var (catalog, provider) = Build();
        provider.Answer = HttpStatusCode.NotFound;
        await Ask(catalog, ModelProbe.IfUnknown);

        var otherKey = new IntegrationEngine { Enabled = true, ApiKey = "другой", Model = "gemini-x" };

        Assert.Equal(ModelState.Unknown, (await Ask(catalog, ModelProbe.CacheOnly, otherKey)).State);
    }
}
