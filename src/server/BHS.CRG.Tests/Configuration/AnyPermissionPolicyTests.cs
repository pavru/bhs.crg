using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using BHS.CRG.Modules;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// «Любое из прав» на адресе (plan-02, ТЗ AUTH-8, AUTH-8.2): политика <c>perm:&lt;а&gt;|&lt;б&gt;</c>
/// открывает адрес носителю КАЖДОГО права списка и закрывает его без обоих.
///
/// <para>Зачем живой хост, а не сборка политики. Политика собирается и без обработчика — и тогда
/// закрыта всем: требование, которое никто не удовлетворяет, отвечает тем же 403, что и настоящий
/// отказ. Различить «работает» и «никому не открывается» можно только запросом носителя права.</para>
///
/// <para>Пользователь без обоих прав нарочно держит ТРЕТЬЕ право пробного модуля: иначе его
/// остановили бы ворота модуля, стоящие перед адресом, и отказ ничего не говорил бы о «любом из».</para>
/// </summary>
public class AnyPermissionPolicyTests
{
    private const string Alpha = "probe.alpha.use";
    private const string Beta = "probe.beta.use";
    private const string Other = "probe.other.use";
    private const string Route = "/api/probe/any-of";

    [Theory]
    [InlineData(Alpha)]
    [InlineData(Beta)]
    public async Task Адрес_открыт_носителю_любого_права_списка(string granted)
    {
        using var host = await StartAsync();

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(host, granted)).StatusCode);
    }

    [Fact]
    public async Task Адрес_закрыт_без_обоих_прав()
    {
        using var host = await StartAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(host, Other)).StatusCode);
    }

    [Fact]
    public async Task Аноним_не_проходит()
    {
        using var host = await StartAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(host, granted: null)).StatusCode);
    }

    /// <summary>
    /// Необъявленный код в списке — отказ при сборке политики (AUTH-8.2), а не дверь, которую
    /// молча открывает второе право. Опечатка в первом коде лишала бы доступа его носителей, а по
    /// ответу приложения дверь выглядела бы рабочей.
    /// </summary>
    [Fact]
    public async Task Необъявленный_код_роняет_сборку_политики()
    {
        using var host = await StartAsync();
        var policies = host.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => policies.GetPolicyAsync(AppPolicies.AnyPermission(Alpha, "probe.alpah.use")));

        Assert.Contains("probe.alpah.use", ex.Message);
    }

    /// <summary>Список из одного права — обычная политика права: имя у них одно.</summary>
    [Fact]
    public void Список_из_одного_права_даёт_обычное_имя() =>
        Assert.Equal(AppPolicies.Permission(Alpha), AppPolicies.AnyPermission(Alpha));

    /// <summary>Пустое место в списке — отказ, как пустое имя у одиночной политики.</summary>
    [Fact]
    public async Task Пустое_место_в_списке_отвергается()
    {
        using var host = await StartAsync();
        var policies = host.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => policies.GetPolicyAsync($"{AppPolicies.PermissionPrefix}{Alpha}||{Beta}"));
    }

    private static async Task<HttpResponseMessage> SendAsync(IHost host, string? granted)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Route);
        if (granted is not null) request.Headers.Add(HeaderAuth.Header, granted);
        return await host.GetTestClient().SendAsync(request);
    }

    private static async Task<IHost> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Modules:Enabled"] = ProbeModule.ModuleCode });

        builder.Services.AddAuthentication(HeaderAuth.Scheme)
            .AddScheme<AuthenticationSchemeOptions, HeaderAuth>(HeaderAuth.Scheme, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IUserPermissions, ClaimPermissions>();
        builder.Services.AddAppModules(builder.Configuration, new ProbeModule());

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapAppModules();
        await app.StartAsync();
        return app;
    }

    /// <summary>Пробный модуль: три права и один адрес под «любым из» первых двух.</summary>
    private sealed class ProbeModule : IAppModule
    {
        public const string ModuleCode = "probe";

        public string Code => ModuleCode;
        public string Title => "Проба «любого из»";

        public IReadOnlyList<AppPermission> Permissions =>
        [
            new(Alpha, "первое право списка", "пробный адрес", ReadAll: ReadAllMark.Out("проба")),
            new(Beta, "второе право списка", "пробный адрес", ReadAll: ReadAllMark.Out("проба")),
            new(Other, "право вне списка", "ворота модуля", ReadAll: ReadAllMark.Out("проба")),
        ];

        public IReadOnlyList<string> RoutePrefixes => ["/api/probe"];
        public void RegisterServices(IServiceCollection services, IConfiguration configuration) { }

        public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
            endpoints.MapGet(Route, () => Results.Ok("открыто"))
                .RequireAuthorization(AppPolicies.AnyPermission(Alpha, Beta));

        public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Вход заголовком: его значение — право пользователя. Без заголовка — аноним.</summary>
    private sealed class HeaderAuth(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Scheme = "probe";
        public const string Header = "X-Probe-Permission";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(Header, out var granted))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity([new Claim(Header, granted.ToString())], Scheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme)));
        }
    }

    /// <summary>Права пользователя — из утверждения, которое положил <see cref="HeaderAuth" />.</summary>
    private sealed class ClaimPermissions : IUserPermissions
    {
        public Task<IReadOnlyCollection<string>> ForAsync(ClaimsPrincipal principal, CancellationToken ct) =>
            Task.FromResult<IReadOnlyCollection<string>>(
                [.. principal.FindAll(HeaderAuth.Header).Select(c => c.Value)]);
    }
}
