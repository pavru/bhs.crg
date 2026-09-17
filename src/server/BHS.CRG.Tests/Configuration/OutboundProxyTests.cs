using System.Net;
using System.Net.Sockets;
using System.Text;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Http;
using MailKit.Net.Proxy;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Прокси для внешних сервисов (issue #936): один прокси в настройках, а пользуется им только сервис
/// с галкой. Путь запроса проверяется настоящими сокетами на петле — поддельным прокси, который
/// записывает, что к нему пришло, — а не догадкой по свойствам обработчика: свойство можно
/// поставить и не получить маршрута.
/// </summary>
public class OutboundProxyTests
{
    // ── Адрес прокси ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://proxy.example:3128", "http://proxy.example:3128/")]
    [InlineData(" socks5://proxy.example:1080 ", "socks5://proxy.example:1080/")]
    [InlineData("SOCKS4A://10.0.0.5:1080", "socks4a://10.0.0.5:1080/")]
    [InlineData("http://proxy.example", "http://proxy.example/")]
    public void Годный_адрес_приводится_к_одному_виду(string input, string expected)
    {
        Assert.True(ProxySettings.TryParseUrl(input, out var uri, out var error), error);
        Assert.Equal(expected, uri!.ToString());
    }

    [Theory]
    [InlineData("", "не задан")]
    [InlineData("proxy.example:3128", "Ожидается")]
    [InlineData("ftp://proxy.example:21", "не поддерживается")]
    [InlineData("https://proxy.example:443", "не поддерживается")]
    [InlineData("http://user:secret@proxy.example:3128", "отдельных полях")]
    [InlineData("http://proxy.example:3128/path", "без пути")]
    [InlineData("socks5://proxy.example", "порт")]
    public void Негодный_адрес_отклоняется_понятной_фразой(string input, string fragment)
    {
        Assert.False(ProxySettings.TryParseUrl(input, out _, out var error));
        Assert.Contains(fragment, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Пароль_наследуется_только_тем_же_прокси_и_логином()
    {
        var saved = new ProxySettings { Url = "http://proxy.example:3128", User = "svc" };
        Assert.True(new ProxySettings { Url = "HTTP://proxy.example:3128/", User = "svc" }.SameProxyAs(saved));
        Assert.False(new ProxySettings { Url = "http://evil.example:3128", User = "svc" }.SameProxyAs(saved));
        Assert.False(new ProxySettings { Url = "http://proxy.example:3128", User = "other" }.SameProxyAs(saved));
        Assert.False(new ProxySettings { Url = "socks5://proxy.example:3128", User = "svc" }.SameProxyAs(saved));
    }

    // ── Кто пользуется прокси ────────────────────────────────────────────────────

    private static OutboundProxyState State(string? url, Action<IntegrationSettingsModel>? flags = null, string? user = null)
    {
        var m = new IntegrationSettingsModel { Proxy = new ProxySettings { Url = url, User = user, Password = user is null ? null : "pw" } };
        flags?.Invoke(m);
        var state = new OutboundProxyState();
        state.Update(m);
        return state;
    }

    [Fact]
    public void Прокси_достаётся_только_сервису_с_галкой()
    {
        var state = State("http://proxy.example:3128", m =>
        {
            m.Recognition["Gemini"] = new IntegrationEngine { UseProxy = true };
            m.Recognition["Ollama"] = new IntegrationEngine { UseProxy = false };
            m.Smtp.UseProxy = true;
        });

        Assert.NotNull(state.ProxyFor(OutboundService.Gemini));
        Assert.NotNull(state.ProxyFor(OutboundService.Smtp));
        Assert.Null(state.ProxyFor(OutboundService.Ollama));
        Assert.Null(state.ProxyFor(OutboundService.Anthropic));
        Assert.Null(state.ProxyFor(OutboundService.ExternalLinks));
    }

    [Fact]
    public void Галка_без_заданного_прокси_значит_напрямую()
    {
        var state = State(null, m => m.Recognition["Gemini"] = new IntegrationEngine { UseProxy = true });
        Assert.Null(state.ProxyFor(OutboundService.Gemini));
    }

    // ── Настоящий путь запроса ───────────────────────────────────────────────────

    /// <summary>
    /// Поддельный прокси на петле: принимает соединения, запоминает первую строку запроса и отвечает
    /// «200 ok». Прямой запрос к цели приходит сюда же, если цель — он сам, поэтому цель в тестах
    /// указывается ДРУГИМ адресом.
    /// </summary>
    private sealed class FakeHttpProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public List<string> RequestLines { get; } = [];
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string Url => $"http://127.0.0.1:{Port}";

        public FakeHttpProxy()
        {
            _listener.Start();
            _ = AcceptLoopAsync();
        }

        private async Task AcceptLoopAsync()
        {
            while (true)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); }
                catch { return; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var stream = client.GetStream();
                        var buffer = new byte[4096];
                        var text = new StringBuilder();
                        while (!text.ToString().Contains("\r\n\r\n"))
                        {
                            var n = await stream.ReadAsync(buffer);
                            if (n == 0) return;
                            text.Append(Encoding.ASCII.GetString(buffer, 0, n));
                        }
                        lock (RequestLines) RequestLines.Add(text.ToString().Split("\r\n")[0]);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
                    }
                });
            }
        }

        public void Dispose() => _listener.Stop();
    }

    [Fact]
    public async Task С_галкой_запрос_уходит_на_прокси()
    {
        using var proxy = new FakeHttpProxy();
        var state = State(proxy.Url, m => m.Recognition["Gemini"] = new IntegrationEngine { UseProxy = true });

        using var handler = new SocketsHttpHandler();
        OutboundConnect.Apply(handler);
        OutboundProxy.Route(handler, OutboundService.Gemini, state);
        using var client = new HttpClient(handler);

        // Имя цели не разрешается нигде — дойти до ответа запрос может только через прокси.
        var body = await client.GetStringAsync("http://generative.invalid/v1/models");

        Assert.Equal("ok", body);
        Assert.Equal("GET http://generative.invalid/v1/models HTTP/1.1", Assert.Single(proxy.RequestLines));
    }

    [Fact]
    public async Task Без_галки_прокси_не_трогается()
    {
        using var proxy = new FakeHttpProxy();
        using var target = new FakeHttpProxy();   // здесь — просто сервер цели
        var state = State(proxy.Url, m => m.Recognition["Gemini"] = new IntegrationEngine { UseProxy = true });

        using var handler = new SocketsHttpHandler();
        OutboundConnect.Apply(handler);
        OutboundProxy.Route(handler, OutboundService.Ollama, state);   // у Ollama галки нет
        using var client = new HttpClient(handler);

        Assert.Equal("ok", await client.GetStringAsync($"{target.Url}/api/tags"));
        Assert.Empty(proxy.RequestLines);
        Assert.Equal("GET /api/tags HTTP/1.1", Assert.Single(target.RequestLines));
    }

    [Fact]
    public async Task Смена_галки_действует_без_пересоздания_клиента()
    {
        using var proxy = new FakeHttpProxy();
        using var target = new FakeHttpProxy();
        var model = new IntegrationSettingsModel { Proxy = new ProxySettings { Url = proxy.Url } };
        var state = new OutboundProxyState();
        state.Update(model);

        using var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.Zero };
        OutboundProxy.Route(handler, OutboundService.Serper, state);
        using var client = new HttpClient(handler);

        await client.GetStringAsync($"{target.Url}/first");
        model.WebSearch["Serper"] = new IntegrationEngine { UseProxy = true };
        state.Update(model);
        await client.GetStringAsync($"{target.Url}/second");

        Assert.Equal("GET /first HTTP/1.1", Assert.Single(target.RequestLines));
        Assert.Equal($"GET {target.Url}/second HTTP/1.1", Assert.Single(proxy.RequestLines));
    }

    [Fact]
    public async Task Загрузка_по_ссылке_через_прокси_не_отклоняется_из_за_адреса_самого_прокси()
    {
        // Прокси на петле — адрес заведомо не публичный. Проверка публичности в момент подключения,
        // применённая к нему, отказала бы на ЛЮБОЙ ссылке общим текстом «ведите на общедоступный
        // ресурс». Цель при прокси проверяет SafeHttpGet заранее, нашим DNS.
        using var proxy = new FakeHttpProxy();
        var state = State(proxy.Url, m => m.ExternalLinksUseProxy = true);

        using var handler = new SocketsHttpHandler();
        OutboundAddressPolicy.ApplyGuard(handler, state);
        using var client = new HttpClient(handler);

        Assert.Equal("ok", await client.GetStringAsync("http://docs.invalid/cert.pdf"));
        Assert.Single(proxy.RequestLines);
    }

    [Fact]
    public async Task Загрузка_по_ссылке_без_галки_по_прежнему_не_пускает_во_внутреннюю_сеть()
    {
        // Сторож того, что исключение для прокси не открыло дверь: галка снята — адрес петли,
        // совпадающий с адресом прокси, отклоняется, как и раньше.
        using var proxy = new FakeHttpProxy();
        var state = State(proxy.Url);

        using var handler = new SocketsHttpHandler();
        OutboundAddressPolicy.ApplyGuard(handler, state);
        using var client = new HttpClient(handler);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => client.GetStringAsync($"{proxy.Url}/"));
        Assert.True(failure is OutboundAddressRefusedException || failure.InnerException is OutboundAddressRefusedException,
            $"ожидался отказ проверки адреса, получили {failure.GetType().Name}: {failure.Message}");
        Assert.Empty(proxy.RequestLines);
    }

    [Fact]
    public async Task Логин_и_пароль_прокси_уходят_ему_при_запросе_авторизации()
    {
        // Прокси, требующий авторизацию: первый ответ 407, второй запрос обязан прийти с заголовком.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var headers = new List<string>();
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var c = await listener.AcceptTcpClientAsync();
                var s = c.GetStream();
                var buf = new byte[4096];
                var sb = new StringBuilder();
                while (!sb.ToString().Contains("\r\n\r\n"))
                {
                    var n = await s.ReadAsync(buf);
                    if (n == 0) break;
                    sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                }
                lock (headers) headers.Add(sb.ToString());
                var reply = sb.ToString().Contains("Proxy-Authorization: Basic ")
                    ? "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"
                    : "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"t\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                await s.WriteAsync(Encoding.ASCII.GetBytes(reply));
            }
        });

        try
        {
            var state = State($"http://127.0.0.1:{port}", m => m.Recognition["Anthropic"] = new IntegrationEngine { UseProxy = true }, user: "svc");
            using var handler = new SocketsHttpHandler();
            OutboundProxy.Route(handler, OutboundService.Anthropic, state);
            using var client = new HttpClient(handler);

            Assert.Equal("ok", await client.GetStringAsync("http://api.invalid/v1/messages"));
            var expected = "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("svc:pw"));
            Assert.Contains(headers, h => h.Contains(expected));
        }
        finally { listener.Stop(); }
    }

    // ── Почта ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://proxy.example:3128", typeof(HttpProxyClient))]
    [InlineData("socks4://proxy.example:1080", typeof(Socks4Client))]
    [InlineData("socks4a://proxy.example:1080", typeof(Socks4aClient))]
    [InlineData("socks5://proxy.example:1080", typeof(Socks5Client))]
    public void Почта_получает_прокси_той_же_схемы(string url, Type expected)
    {
        var client = OutboundProxy.ForMailKit(State(url, user: "svc"));
        Assert.IsType(expected, client);
        Assert.Equal("proxy.example", client!.ProxyHost);
        Assert.Equal("svc", client.ProxyCredentials?.UserName);
    }

    [Fact]
    public void Почта_без_заданного_прокси_идёт_напрямую()
        => Assert.Null(OutboundProxy.ForMailKit(State(null)));
}
