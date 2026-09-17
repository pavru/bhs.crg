using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Http;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Прокси в настройках (issue #936): хранение пароля, его судьба при смене прокси, отказ на негодном
/// адресе и то, что сохранённое действует сразу — без перезапуска и без первого случайного читателя.
/// </summary>
[Collection("Integration")]
public class ProxySettingsTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private const string ProxySecret = "proxy-pass-5e1a-MUST-NOT-BE-STORED-IN-PLAIN-TEXT";

    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();

    public async Task DisposeAsync()
    {
        using var scope = fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().IntegrationSettings.ExecuteDeleteAsync();
        var settings = scope.ServiceProvider.GetRequiredService<IIntegrationSettings>();
        settings.Invalidate();
        // Состояние прокси — на процесс: соседям нельзя оставить «Gemini через прокси».
        await settings.GetEffectiveAsync();
    }

    [Fact]
    public async Task Пароль_прокси_хранится_зашифрованным()
    {
        using var scope = fixture.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IIntegrationSettings>();
        await settings.SaveProxyAsync(new ProxySettings { Url = "http://proxy.example:3128", User = "svc", Password = ProxySecret });

        var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().IntegrationSettings.AsNoTracking().FirstAsync();
        Assert.DoesNotContain(ProxySecret, row.Data.RootElement.GetRawText());
        Assert.Equal(ProxySecret, (await settings.GetEffectiveAsync()).Proxy.Password);
    }

    [Theory]
    [InlineData("http://evil.example:3128", "svc")]
    [InlineData("http://proxy.example:3128", "other")]
    [InlineData("socks5://proxy.example:1080", "svc")]
    public async Task Другой_прокси_с_пустым_паролем_не_получает_сохранённый(string url, string user)
    {
        using var scope = fixture.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IIntegrationSettings>();
        await settings.SaveProxyAsync(new ProxySettings { Url = "http://proxy.example:3128", User = "svc", Password = ProxySecret });
        await settings.SaveProxyAsync(new ProxySettings { Url = url, User = user, Password = null });

        Assert.True(string.IsNullOrEmpty((await settings.GetEffectiveAsync()).Proxy.Password));
    }

    [Fact]
    public async Task Тот_же_прокси_с_пустым_паролем_сохраняет_прежний()
    {
        using var scope = fixture.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IIntegrationSettings>();
        await settings.SaveProxyAsync(new ProxySettings { Url = "http://proxy.example:3128", User = "svc", Password = ProxySecret });
        await settings.SaveProxyAsync(new ProxySettings { Url = " HTTP://proxy.example:3128/ ", User = "svc", Password = null });

        Assert.Equal(ProxySecret, (await settings.GetEffectiveAsync()).Proxy.Password);
    }

    [Fact]
    public async Task Негодный_адрес_отклоняется_при_сохранении()
    {
        using var scope = fixture.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IIntegrationSettings>();
        var ex = await Assert.ThrowsAsync<InvalidRequestException>(() =>
            settings.SaveProxyAsync(new ProxySettings { Url = "http://user:pw@proxy.example:3128" }));
        Assert.Contains("отдельных полях", ex.Message);
    }

    [Fact]
    public async Task Сохранённые_прокси_и_галка_действуют_сразу()
    {
        using var scope = fixture.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IIntegrationSettings>();
        var state = scope.ServiceProvider.GetRequiredService<OutboundProxyState>();

        await settings.SaveProxyAsync(new ProxySettings { Url = "socks5://proxy.example:1080" });
        Assert.Null(state.ProxyFor(OutboundService.Gemini));

        // Никто не читает настройки между сохранением и проверкой: действовать обязано само сохранение.
        await settings.SaveAsync(new IntegrationSettingsModel
        {
            Recognition = { ["Gemini"] = new IntegrationEngine { Enabled = true, UseProxy = true } },
            ExternalLinksUseProxy = true,
        });
        Assert.Equal("socks5://proxy.example:1080/", state.ProxyFor(OutboundService.Gemini)?.ToString());
        Assert.NotNull(state.ProxyFor(OutboundService.ExternalLinks));
        Assert.Null(state.ProxyFor(OutboundService.Anthropic));
    }

    // ── Проверка связи по кнопке (issue #937) ────────────────────────────────────

    [Fact]
    public async Task Проверка_чужого_прокси_с_пустым_паролем_отклоняется()
    {
        // Иначе кнопка «Проверить» — это способ унести сохранённый пароль: вписать свой прокси,
        // поле пароля не трогать, и первый же запрос принесёт его на чужой адрес (урок SMTP).
        using var scope = fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IIntegrationSettings>()
            .SaveProxyAsync(new ProxySettings { Url = "http://proxy.example:3128", User = "svc", Password = ProxySecret });

        using var listener = new Listener();
        var answer = await CheckAsync(new { url = listener.Url, user = "svc" });

        Assert.False(answer.GetProperty("ok").GetBoolean());
        Assert.Contains("пароль", answer.GetProperty("message").GetString()!);
        Assert.Equal(0, listener.Accepted);   // до чужого адреса проверка даже не дошла
    }

    [Fact]
    public async Task Проверка_без_сохранённого_пароля_не_требует_его()
    {
        // Прокси без входа — обычное дело, и отказ «введите пароль» на пустом месте выглядел бы
        // поломкой. Сторож против того, чтобы правило о пароле сработало там, где пароля нет.
        using var listener = new Listener();
        var answer = await CheckAsync(new { url = listener.Url });

        Assert.True(answer.GetProperty("ok").GetBoolean(), answer.GetProperty("message").GetString());
        Assert.Contains("Туннель не проверяли", answer.GetProperty("message").GetString()!);
        Assert.Equal(1, listener.Accepted);
    }

    [Fact]
    public async Task Проверка_идёт_по_значениям_формы_а_не_по_сохранённым()
    {
        // Проверять до сохранения — обычный порядок; проверка сохранённого адреса вместо набранного
        // отвечала бы не про то, что человек видит на экране.
        using var scope = fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IIntegrationSettings>()
            .SaveProxyAsync(new ProxySettings { Url = $"http://127.0.0.1:{DeadPort()}" });

        using var listener = new Listener();
        var answer = await CheckAsync(new { url = listener.Url });

        Assert.True(answer.GetProperty("ok").GetBoolean(), answer.GetProperty("message").GetString());
        Assert.Equal(1, listener.Accepted);
    }

    [Fact]
    public async Task Проверка_неподнятого_прокси_называет_его_недоступным()
    {
        var answer = await CheckAsync(new { url = $"http://127.0.0.1:{DeadPort()}" });

        Assert.False(answer.GetProperty("ok").GetBoolean());
        Assert.Equal(nameof(OutboundProblem.ProxyUnreachable), answer.GetProperty("problem").GetString());
    }

    private async Task<JsonElement> CheckAsync(object body)
    {
        var client = await AdminClientAsync();
        var resp = await client.PostAsJsonAsync("/api/settings/integrations/proxy/test", body);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Клиент с токеном администратора: настройки читает и правит только эта роль.</summary>
    private async Task<HttpClient> AdminClientAsync()
    {
        var email = $"admin_{Guid.NewGuid():N}@test.local";
        const string password = "Passw0rd!";
        using (var scope = fixture.Services.CreateScope())
        {
            var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var rm = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            if (!await rm.RoleExistsAsync("Admin")) Assert.True((await rm.CreateAsync(new IdentityRole<Guid>("Admin"))).Succeeded);
            var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "Админ", EmailConfirmed = true };
            Assert.True((await um.CreateAsync(user, password)).Succeeded);
            Assert.True((await um.AddToRoleAsync(user, "Admin")).Succeeded);
        }

        var client = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Слушатель на петле вместо прокси: считает, сколько раз к нему пришли.</summary>
    private sealed class Listener : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private int _accepted;

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        public int Accepted => Volatile.Read(ref _accepted);

        public Listener()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(); }
                    catch { return; }
                    Interlocked.Increment(ref _accepted);
                    client.Dispose();
                }
            });
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
}
