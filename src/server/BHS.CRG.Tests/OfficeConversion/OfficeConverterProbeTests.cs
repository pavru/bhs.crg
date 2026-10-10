using System.Net;
using BHS.CRG.Infrastructure.OfficeConversion;

namespace BHS.CRG.Tests.OfficeConversion;

/// <summary>
/// Проба конвертера офисных файлов для мониторинга здоровья (issue #1267).
///
/// <para>Держит три вещи: без адреса конвертера нет и проверять нечего; спрашивается его
/// собственная проверка здоровья, а не открытый порт; отказ сервиса назван причиной с кодом, а
/// не принят за готовность.</para>
/// </summary>
public class OfficeConverterProbeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Без_адреса_конвертера_у_экземпляра_нет(string? baseUrl)
        => Assert.False(Probe(baseUrl, _ => throw new InvalidOperationException("звать некого")).Configured);

    [Theory]
    [InlineData("http://converter:3000")]
    [InlineData("http://converter:3000/")]
    [InlineData(" http://converter:3000 ")]
    public async Task Спрашивается_проверка_здоровья_самого_сервиса(string baseUrl)
    {
        Uri? asked = null;
        var probe = Probe(baseUrl, request =>
        {
            asked = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        Assert.True(probe.Configured);
        Assert.Null(await probe.WhyNotReadyAsync(CancellationToken.None));

        Assert.Equal("http://converter:3000/health", asked?.ToString());
    }

    /// <summary>Обёртка жива, а LibreOffice под ней не поднялся — сервис отвечает 503, и это отказ.</summary>
    [Fact]
    public async Task Отказ_сервиса_назван_причиной_с_кодом()
    {
        var probe = Probe("http://converter:3000", _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        Assert.Equal("Конвертер ответил 503", await probe.WhyNotReadyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Недоступный_сервис_не_принят_за_готовый()
    {
        var probe = Probe("http://converter:3000", _ => throw new HttpRequestException("Connection refused"));

        await Assert.ThrowsAsync<HttpRequestException>(() => probe.WhyNotReadyAsync(CancellationToken.None));
    }

    private static OfficeConverterProbe Probe(string? baseUrl, Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(new Clients(respond), new OfficeConverterOptions { BaseUrl = baseUrl });

    private sealed class Clients(Func<HttpRequestMessage, HttpResponseMessage> respond) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(OfficeConverterOptions.ClientName, name);
            return new HttpClient(new Handler(respond));
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }
}
