using System.Net;
using System.Text;
using BHS.CRG.Infrastructure.Plugins;
using Microsoft.Extensions.Logging.Abstractions;

namespace BHS.CRG.Tests.Plugins;

/// <summary>Прогрев схем HTTP-плагина (GET /schemas): успех заполняет ProvidedSchemas, отказ — best-effort пусто.</summary>
public class HttpDataSourcePluginTests
{
    private static HttpDataSourcePlugin Plugin(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var http = new HttpClient(new StubHandler(responder)) { BaseAddress = new Uri("http://plugin.test") };
        return new HttpDataSourcePlugin(new HttpPluginConfig { Id = "p1", DisplayName = "P1", BaseUrl = "http://plugin.test" }, http);
    }

    [Fact]
    public async Task FetchSchemas_Success_PopulatesProvidedSchemas()
    {
        var plugin = Plugin(req =>
        {
            Assert.EndsWith("/schemas", req.RequestUri!.AbsolutePath);
            return Json("""[{"entityType":"org","displayName":"Организация","fieldsSchema":{"fields":[]}}]""");
        });

        await plugin.FetchSchemasAsync(NullLogger.Instance);

        Assert.Single(plugin.ProvidedSchemas);
        Assert.Equal("org", plugin.ProvidedSchemas[0].EntityType);
        Assert.Equal("Организация", plugin.ProvidedSchemas[0].DisplayName);
    }

    [Fact]
    public async Task FetchSchemas_PluginUnavailable_StaysEmpty()
    {
        var plugin = Plugin(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await plugin.FetchSchemasAsync(NullLogger.Instance); // не бросает

        Assert.Empty(plugin.ProvidedSchemas);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(responder(request));
    }
}
