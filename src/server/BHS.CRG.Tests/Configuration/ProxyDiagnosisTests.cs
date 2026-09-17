using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Http;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Разбор отказов при прокси (issue #937): кто именно отказал и что делать.
///
/// Проверяется НАСТОЯЩИМИ отказами — поддельный прокси на петле отвечает 407, 403 и 502 на CONNECT,
/// подсовывает свой сертификат, не поднимается вовсе, — а не заранее собранными исключениями. Иначе
/// проверялось бы наше представление о том, как .NET сообщает о беде, а оно и есть то, в чём легче
/// всего ошибиться: сообщения и типы исключений тут чужие.
/// </summary>
public class ProxyDiagnosisTests
{
    // ── Поддельный прокси ────────────────────────────────────────────────────────

    /// <summary>Что поддельный прокси делает с запросом CONNECT.</summary>
    private enum Reply { Status, Intercept }

    private sealed class FakeProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Reply _mode;
        private readonly string _status;
        private readonly X509Certificate2? _cert;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string Url => $"http://127.0.0.1:{Port}";

        private FakeProxy(Reply mode, string status, X509Certificate2? cert)
        {
            (_mode, _status, _cert) = (mode, status, cert);
            _listener.Start();
            _ = AcceptLoopAsync();
        }

        /// <summary>Прокси, отвечающий на CONNECT заданным кодом.</summary>
        public static FakeProxy Answering(string status) => new(Reply.Status, status, null);

        /// <summary>Прокси, который туннель открывает, но дальше говорит с нами СВОИМ сертификатом.</summary>
        public static FakeProxy Intercepting() => new(Reply.Intercept, "200 Connection established", SelfSigned());

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
                        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {_status}\r\nContent-Length: 0\r\n\r\n"));
                        if (_mode != Reply.Intercept) return;
                        try
                        {
                            using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
                            await tls.AuthenticateAsServerAsync(_cert!);
                        }
                        catch { /* клиент откажется от нашего сертификата — этого мы и ждём */ }
                    }
                });
            }
        }

        /// <summary>Свой сертификат «для сервиса» — ровно то, чем прокси с проверкой TLS и подменяет чужой.</summary>
        private static X509Certificate2 SelfSigned()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=service.invalid", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            // Через PFX: в Windows SslStream не берёт сертификат с эфемерным ключом напрямую.
            return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
        }

        public void Dispose() => _listener.Stop();
    }

    /// <summary>Порт, на котором заведомо никого нет: слушателя подняли и закрыли.</summary>
    private static int DeadPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static OutboundProxyState Via(string url) =>
        OutboundProxyState.ForCheck(new ProxySettings { Url = url }, OutboundService.Gemini);

    private static async Task<Exception> FailureAsync(OutboundProxyState state, string? target = null)
    {
        using var handler = new SocketsHttpHandler();
        OutboundProxy.Route(handler, OutboundService.Gemini, state);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        return await Record.ExceptionAsync(() => client.GetStringAsync(target ?? "https://service.invalid/v1/models"))
               ?? throw new InvalidOperationException("запрос неожиданно удался");
    }

    // ── Диагнозы ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Прокси_не_поднят_отличается_от_недоступного_сервиса()
    {
        var state = Via($"http://127.0.0.1:{DeadPort()}");
        var problem = OutboundDiagnosis.Diagnose(await FailureAsync(state), OutboundService.Gemini, state);

        Assert.Equal(OutboundProblem.ProxyUnreachable, problem);
        Assert.Contains("Прокси", OutboundDiagnosis.Explain(problem, OutboundService.Gemini, state));
    }

    [Theory]
    [InlineData("407 Proxy Authentication Required", OutboundProblem.ProxyAuth)]
    [InlineData("403 Forbidden", OutboundProblem.ProxyForbidden)]
    [InlineData("502 Bad Gateway", OutboundProblem.ProxyUpstream)]
    [InlineData("503 Service Unavailable", OutboundProblem.ProxyUpstream)]
    public async Task Отказ_прокси_на_туннеле_читается_по_его_коду(string status, OutboundProblem expected)
    {
        using var proxy = FakeProxy.Answering(status);
        var state = Via(proxy.Url);

        Assert.Equal(expected, OutboundDiagnosis.Diagnose(await FailureAsync(state), OutboundService.Gemini, state));
    }

    [Fact]
    public async Task Подмена_сертификата_названа_подменой_а_не_отказом_сервиса()
    {
        using var proxy = FakeProxy.Intercepting();
        var state = Via(proxy.Url);
        var problem = OutboundDiagnosis.Diagnose(await FailureAsync(state), OutboundService.Gemini, state);

        Assert.Equal(OutboundProblem.ProxyTlsIntercepted, problem);
        Assert.Contains("сертификат", OutboundDiagnosis.Explain(problem, OutboundService.Gemini, state));
    }

    [Fact]
    public async Task Обрыв_после_туннеля_не_выдаётся_за_подмену_сертификата()
    {
        // Прокси согласился на CONNECT и закрыл соединение. Для .NET это такой же «отказ
        // защищённого соединения», как и отвергнутый сертификат, и соблазн назвать подменой велик —
        // но совет ставить корневой сертификат отправил бы администратора чинить несуществующее.
        using var proxy = FakeProxy.Answering("200 Connection established");
        var state = Via(proxy.Url);
        var failure = await FailureAsync(state);

        Assert.NotEqual(OutboundProblem.ProxyTlsIntercepted,
            OutboundDiagnosis.Diagnose(failure, OutboundService.Gemini, state));
        // Но и умолчать о прокси нельзя: разбор начнётся не с того конца.
        Assert.Contains("Через прокси", OutboundDiagnosis.Describe(failure, OutboundService.Gemini, state));
    }

    [Fact]
    public async Task Снятая_галка_при_настроенном_прокси_названа_прямо()
    {
        // Сервис ходит напрямую, прямого выхода нет, а прокси в системе есть. Без этого диагноза
        // отказ выглядит как «сервис недоступен», и настоящая причина — снятая галка — не видна.
        var state = OutboundProxyState.ForCheck(new ProxySettings { Url = "http://proxy.example:3128" });
        using var handler = new SocketsHttpHandler();
        OutboundProxy.Route(handler, OutboundService.Gemini, state);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

        var failure = await Record.ExceptionAsync(() => client.GetStringAsync($"http://127.0.0.1:{DeadPort()}/v1/models"));
        var problem = OutboundDiagnosis.Diagnose(failure!, OutboundService.Gemini, state);

        Assert.Equal(OutboundProblem.ProxyOffForService, problem);
        Assert.Contains("Через прокси", OutboundDiagnosis.Explain(problem, OutboundService.Gemini, state));
    }

    [Fact]
    public async Task Без_прокси_отказ_остаётся_обычным()
    {
        // Сторож против услужливости классификатора: где прокси нет, придумывать его нельзя.
        var state = new OutboundProxyState();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var failure = await Record.ExceptionAsync(() => client.GetStringAsync($"http://127.0.0.1:{DeadPort()}/v1/models"));

        Assert.Equal(OutboundProblem.None, OutboundDiagnosis.Diagnose(failure!, OutboundService.Gemini, state));
    }

    [Fact]
    public void Ответ_407_разбирается_и_без_исключения()
    {
        // По незашифрованному HTTP прокси отвечает не отказом соединения, а обычным ответом, и тот
        // доезжает до вызывающего: разбирать его надо отдельно.
        var state = Via("http://proxy.example:3128");
        Assert.Equal(OutboundProblem.ProxyAuth, OutboundDiagnosis.DiagnoseStatus(407, OutboundService.Gemini, state));
        // 403 от туннелированного сервиса — его собственный ответ, и прокси тут ни при чём.
        Assert.Equal(OutboundProblem.None, OutboundDiagnosis.DiagnoseStatus(403, OutboundService.Gemini, state));
    }

    [Fact]
    public async Task SOCKS_отказавший_в_логине_назван_отказом_в_логине()
    {
        // У SOCKS кода ответа, похожего на HTTP, нет: он сообщает о себе словами, и разобрать их —
        // единственный способ отличить «не пустил по логину» от «не отвечает». Поэтому проверяем
        // настоящим SOCKS5-сервером, а не выдуманным исключением.
        using var socks = new FakeSocks5();
        var state = OutboundProxyState.ForCheck(
            new ProxySettings { Url = socks.Url, User = "svc", Password = "pw" }, OutboundService.Gemini);

        Assert.Equal(OutboundProblem.ProxyAuth,
            OutboundDiagnosis.Diagnose(await FailureAsync(state), OutboundService.Gemini, state));
    }

    /// <summary>SOCKS5, который требует логин с паролем и любой отвергает.</summary>
    private sealed class FakeSocks5 : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public string Url => $"socks5://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        public FakeSocks5()
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
                        var buffer = new byte[512];
                        if (await stream.ReadAsync(buffer) == 0) return;      // приветствие
                        await stream.WriteAsync(new byte[] { 0x05, 0x02 });   // «вход по логину и паролю»
                        if (await stream.ReadAsync(buffer) == 0) return;      // логин с паролем
                        await stream.WriteAsync(new byte[] { 0x01, 0x01 });   // отказ
                    }
                });
            }
        }

        public void Dispose() => _listener.Stop();
    }

    // ── Проверка по кнопке ───────────────────────────────────────────────────────

    [Fact]
    public async Task Проверка_доходит_до_цели_через_прокси()
    {
        using var proxy = FakeProxy.Answering("200 OK");
        var result = await ProxyCheck.RunAsync(new ProxySettings { Url = proxy.Url },
            OutboundService.Gemini, new Uri("http://service.invalid/v1/models"), default);

        Assert.True(result.Ok, result.Message);
        Assert.Contains("200", result.Message);
    }

    [Fact]
    public async Task Проверка_без_галок_проверяет_сам_прокси_и_говорит_об_этом()
    {
        using var proxy = FakeProxy.Answering("200 OK");
        var result = await ProxyCheck.RunAsync(new ProxySettings { Url = proxy.Url }, null, null, default);

        Assert.True(result.Ok, result.Message);
        Assert.Contains("Туннель не проверяли", result.Message);
    }

    [Fact]
    public async Task Прокси_с_несколькими_адресами_проверяется_по_живому()
    {
        // «localhost» — это и ::1, и 127.0.0.1. Поймано живьём: слушатель отвечал только на
        // 127.0.0.1, платформа перебирала адреса по очереди (issue #917), и рабочий прокси
        // объявлялся недоступным через все двенадцать секунд срока.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () => { try { while (true) (await listener.AcceptTcpClientAsync()).Dispose(); } catch { } });
        try
        {
            var result = await ProxyCheck.RunAsync(new ProxySettings { Url = $"http://localhost:{port}" }, null, null, default);
            Assert.True(result.Ok, result.Message);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Проверка_неподнятого_прокси_называет_прокси_а_не_сервис()
    {
        var result = await ProxyCheck.RunAsync(new ProxySettings { Url = $"http://127.0.0.1:{DeadPort()}" },
            OutboundService.Gemini, new Uri("http://service.invalid/"), default);

        Assert.False(result.Ok);
        Assert.Equal(OutboundProblem.ProxyUnreachable, result.Problem);
        Assert.DoesNotContain("Gemini", result.Message);
    }

    [Fact]
    public async Task Проверка_негодного_адреса_не_ходит_никуда()
    {
        var result = await ProxyCheck.RunAsync(new ProxySettings { Url = "не адрес" },
            OutboundService.Gemini, new Uri("http://service.invalid/"), default);

        Assert.False(result.Ok);
        Assert.Contains("не похоже на адрес прокси", result.Message);
    }

    // ── Секреты в текстах ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Ошибка на http://svc:s3cret@proxy.example:3128/", "http://***:***@proxy.example:3128/")]
    [InlineData("socks5://user:pw@10.0.0.5:1080 не отвечает", "socks5://***:***@10.0.0.5:1080")]
    public void Логин_и_пароль_в_тексте_прячутся(string text, string expected)
    {
        var masked = OutboundDiagnosis.Mask(text);
        Assert.Contains(expected, masked);
        Assert.DoesNotContain("s3cret", masked);
        Assert.DoesNotContain(":pw@", masked);
    }

    [Fact]
    public async Task Текст_отказа_уходит_наружу_без_пароля()
    {
        // Сквозная проверка: маскирование стоит на пути, по которому текст и попадает в уведомление.
        using var proxy = FakeProxy.Answering("407 Proxy Authentication Required");
        var state = OutboundProxyState.ForCheck(
            new ProxySettings { Url = proxy.Url, User = "svc", Password = "s3cret" }, OutboundService.Gemini);

        var described = OutboundDiagnosis.Describe(await FailureAsync(state), OutboundService.Gemini, state);

        Assert.Contains("требует вход", described);
        Assert.DoesNotContain("s3cret", described);
    }
}
