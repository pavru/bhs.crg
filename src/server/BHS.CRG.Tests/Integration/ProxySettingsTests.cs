using BHS.CRG.Application.Common;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Http;
using BHS.CRG.Infrastructure.Persistence;
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
}
