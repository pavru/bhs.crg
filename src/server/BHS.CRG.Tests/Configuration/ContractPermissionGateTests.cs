using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using BHS.CRG.Modules;
using Microsoft.AspNetCore.Authentication;
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
/// Ворота на праве контракта (change <c>plan-21-contract-permissions</c>, ТЗ AUTH-8, AUTH-8.1) — на
/// лёгком хосте с пробным модулем, объявившим одно обычное право и одно право контракта.
///
/// <para>Здесь то, чего не показать на живом хосте без пробного модуля в составе поставки: адрес ВНЕ
/// группы модуля-владельца под <c>perm:</c> на праве контракта. Он открыт носителю права — ради этого
/// право и выдают, — а адрес группы тому же человеку закрыт воротами модуля. Ответ доступа и
/// уведомления — в <c>Integration/ContractPermissionTests</c>.</para>
/// </summary>
public class ContractPermissionGateTests
{
    private const string Ordinary = "probe.thing.read";
    private const string Contract = "probe.thing.share";
    private const string InGroup = "/api/probe/thing";
    private const string OutsideGroup = "/api/consumer/thing";

    [Fact]
    public async Task Право_контракта_открывает_адрес_вне_группы_владельца()
    {
        using var host = await StartAsync();

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(host, OutsideGroup, Contract)).StatusCode);
    }

    /// <summary>
    /// Адрес группы стоит под тем же правом контракта — и всё равно закрыт: впереди ворота модуля,
    /// а их право контракта не проходит. Без этого право, выданное «Монтажнику» ради подсказки,
    /// открыло бы ему всю дверь сметы, где на адресе стоит то же право.
    /// </summary>
    [Fact]
    public async Task Право_контракта_не_проходит_ворота_модуля_владельца()
    {
        using var host = await StartAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(host, InGroup, Contract)).StatusCode);
    }

    [Fact]
    public async Task С_обычным_правом_модуль_открыт()
    {
        using var host = await StartAsync();

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(host, InGroup, Contract, Ordinary)).StatusCode);
    }

    private static async Task<HttpResponseMessage> SendAsync(IHost host, string route, params string[] granted)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.Add(HeaderAuth.Header, string.Join(",", granted));
        return await host.GetTestClient().SendAsync(request);
    }

    private static async Task<IHost> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Modules:Enabled"] = ProbeModule.ModuleCode });

        builder.Services.AddAuthentication(HeaderAuth.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, HeaderAuth>(HeaderAuth.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IUserPermissions, ClaimPermissions>();
        builder.Services.AddAppModules(builder.Configuration, new ProbeModule());

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapAppModules();
        // Адрес другого модуля (здесь — ядра), которому право контракта и выдано.
        app.MapGet(OutsideGroup, () => Results.Ok("открыто"))
            .RequireAuthorization(AppPolicies.Permission(Contract));
        await app.StartAsync();
        return app;
    }

    /// <summary>Пробный модуль: обычное право, право контракта и адрес под правом контракта.</summary>
    private sealed class ProbeModule : IAppModule
    {
        public const string ModuleCode = "probe";

        public string Code => ModuleCode;
        public string Title => "Проба права контракта";

        public IReadOnlyList<AppPermission> Permissions =>
        [
            new(Ordinary, "видеть вещи", "вещи модуля", ReadAll: ReadAllMark.In),
            new(Contract, "отдавать вещи другому модулю", "вещи модуля без цен",
                ReadAll: ReadAllMark.Out("выдача другому модулю"), Contract: true),
        ];

        public IReadOnlyList<string> RoutePrefixes => ["/api/probe"];
        public void RegisterServices(IServiceCollection services, IConfiguration configuration) { }

        public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
            endpoints.MapGet(InGroup, () => Results.Ok("открыто"))
                .RequireAuthorization(AppPolicies.Permission(Contract));

        public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Вход заголовком: права пользователя через запятую.</summary>
    private sealed class HeaderAuth(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "probe";
        public const string Header = "X-Probe-Permissions";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(Header, out var granted))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(
                granted.ToString().Split(',').Select(code => new Claim(Header, code)), SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }

    private sealed class ClaimPermissions : IUserPermissions
    {
        public Task<IReadOnlyCollection<string>> ForAsync(ClaimsPrincipal principal, CancellationToken ct) =>
            Task.FromResult<IReadOnlyCollection<string>>(
                [.. principal.FindAll(HeaderAuth.Header).Select(c => c.Value)]);
    }
}
