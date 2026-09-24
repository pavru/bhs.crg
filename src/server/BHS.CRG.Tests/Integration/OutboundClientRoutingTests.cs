using System.Net;
using BHS.CRG.Infrastructure.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Сторож маршрутов исходящих клиентов (issue #936). У каждого клиента фабрики должно быть сказано,
/// чей он: внешнего сервиса с галкой «через прокси» или внутренний, всегда напрямую.
///
/// Без сторожа новый клиент молча получал бы умолчание — «напрямую», — и галка его сервиса
/// сохранялась бы, показывалась бы включённой и не действовала: в сети «только через прокси» это
/// выглядело бы как «сервис недоступен». Проверяется собранный обработчик настоящего приложения, а
/// не регистрации в тексте: регистрацию можно написать и не получить маршрута.
/// </summary>
[Collection("Integration")]
public class OutboundClientRoutingTests(IntegrationTestFixture fixture)
{
    /// <summary>Сервис клиента; <c>null</c> — внутренний, всегда напрямую. Новый клиент вписывается сюда осознанно.</summary>
    private static readonly Dictionary<string, OutboundService?> Expected = new()
    {
        ["GeminiRecognizerEngine"] = OutboundService.Gemini,
        ["AnthropicRecognizerEngine"] = OutboundService.Anthropic,
        ["OllamaRecognizerEngine"] = OutboundService.Ollama,
        ["SerperEngine"] = OutboundService.Serper,
        ["YandexEngine"] = OutboundService.Yandex,
        ["GithubIssueClient"] = OutboundService.Github,
        ["update-check"] = OutboundService.UpdateCheck,
        ["model-catalog:Gemini"] = OutboundService.Gemini,
        ["model-catalog:Anthropic"] = OutboundService.Anthropic,
        ["model-catalog:Ollama"] = OutboundService.Ollama,
        ["health:Gemini"] = OutboundService.Gemini,
        ["health:Ollama"] = OutboundService.Ollama,
        ["TieredWebSearch"] = OutboundService.ExternalLinks,
        ["IFileUrlFetcher"] = OutboundService.ExternalLinks,
    };

    private IReadOnlyList<string> RegisteredNames() =>
        fixture.Services.GetServices<IConfigureOptions<HttpClientFactoryOptions>>()
            .OfType<ConfigureNamedOptions<HttpClientFactoryOptions>>()
            .Select(o => o.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Distinct()
            .ToList();

    private SocketsHttpHandler PrimaryHandler(string name)
    {
        HttpMessageHandler h = fixture.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(name);
        while (h is DelegatingHandler d) h = d.InnerHandler!;
        return Assert.IsType<SocketsHttpHandler>(h);
    }

    [Fact]
    public void Каждый_клиент_фабрики_знает_свой_маршрут()
    {
        var unknown = RegisteredNames().Where(n => !Expected.ContainsKey(n)).ToList();
        Assert.True(unknown.Count == 0,
            "Клиенты без маршрута: " + string.Join(", ", unknown) + ".\n" +
            "Скажите, чей клиент: внешнему сервису — RouteVia(..., OutboundService.X) при регистрации " +
            "(Configuration/ServiceRegistration.*.cs), " +
            "внутреннему — ничего, но впишите его сюда со значением null.");

        // И обратное: сторож не должен молча устареть, проверяя клиентов, которых больше нет.
        var gone = Expected.Keys.Except(RegisteredNames()).ToList();
        Assert.True(gone.Count == 0, "В ожидаемых есть незарегистрированные клиенты: " + string.Join(", ", gone));
    }

    [Fact]
    public void Клиент_внешнего_сервиса_спрашивает_прокси_своего_сервиса_а_внутренний_ходит_напрямую()
    {
        foreach (var (name, service) in Expected)
        {
            var handler = PrimaryHandler(name);
            if (service is null)
            {
                Assert.False(handler.UseProxy, $"{name}: внутренний клиент не должен знать о прокси");
                continue;
            }
            Assert.True(handler.UseProxy, $"{name}: у клиента выключен прокси");
            var proxy = Assert.IsType<ServiceWebProxy>(handler.Proxy);
            Assert.True(proxy.Service == service, $"{name}: прокси сервиса {proxy.Service}, ожидался {service}");
        }
    }

    [Fact]
    public void Без_имени_и_в_окружении_прокси_не_действует()
    {
        // Клиент без имени — умолчание для всего, что не сказало о себе: напрямую.
        Assert.False(PrimaryHandler(Options.DefaultName).UseProxy);
        // Прокси процесса пуст: SDK хранилища и любой new HttpClient() не возьмут HTTP(S)_PROXY из окружения.
        var target = new Uri("http://garage:3900/");
        Assert.True(HttpClient.DefaultProxy.IsBypassed(target) || HttpClient.DefaultProxy.GetProxy(target) is null
                    || HttpClient.DefaultProxy.GetProxy(target) == target,
            "HttpClient.DefaultProxy ведёт в прокси — переменные окружения снова действуют");
        Assert.IsType<WebProxy>(HttpClient.DefaultProxy);
    }
}
