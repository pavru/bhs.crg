using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Выключенный модуль: данные остаются, адреса отказывают с названной причиной (issue #943,
/// ТЗ OVW-10, AUTH-15, AUTH-19).
///
/// Тест не про «модуль не работает» — это и так очевидно, — а про то, ЧЕМ он отвечает. Разница
/// между пустым 404 и отказом, который называет причину, ложится на человека: по 404 он думает,
/// что ошибся ссылкой, и ищет опечатку, а не выключенный модуль. В интерфейсе из этого же различия
/// вырастает честная страница «нужен модуль X» вместо бесконечной загрузки.
/// </summary>
public class DisabledModuleTests
{
    /// <summary>
    /// Адрес выключенного модуля отвечает отказом, и в ответе названы и причина, и модуль.
    ///
    /// Код 501: экземпляр не умеет того, о чём просят, и не научится сам — это не «нет такого
    /// адреса» (404, неотличимо от опечатки), не «вам нельзя» (403, перекладывает причину на права
    /// пользователя) и не «попробуйте позже» (503).
    /// </summary>
    [Fact]
    public async Task Disabled_module_refuses_and_names_the_reason()
    {
        using var host = await StartAsync(enabled: "id", new IdLike(), new CostsLike());
        var client = host.GetTestClient();

        var response = await client.GetAsync("/api/costs/invoices");

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("costs", body.GetProperty("module").GetString());
        Assert.Contains("не подключён", body.GetProperty("error").GetString());
        Assert.Contains("Счета и накладные", body.GetProperty("error").GetString());
    }

    /// <summary>
    /// Адрес ядра под тем же префиксом, что у выключенного модуля, продолжает работать.
    ///
    /// Это причина, по которой отказ вешается на маршруты модуля, а не на его префикс. Печатные
    /// формы исполнительной документации живут под <c>/api/document-sets</c> — там же, где общие
    /// адреса комплектов. Заглушка на префикс унесла бы вместе с модулем часть ядра, и заметили бы
    /// это не при выключении модуля, а когда у кого-то пропал справочник.
    /// </summary>
    [Fact]
    public async Task Core_routes_under_the_same_prefix_keep_working()
    {
        using var host = await StartAsync(
            enabled: "id",
            configureCore: app => app.MapGet("/api/document-sets/search", () => Results.Ok("ядро отвечает")),
            new IdLike(), new SharedPrefixModule());

        var client = host.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/document-sets/search")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotImplemented,
            (await client.GetAsync("/api/document-sets/1/print-form")).StatusCode);
    }

    /// <summary>
    /// Выключенный модуль не регистрирует служб и не инициализируется — то есть в базе от него
    /// ничего не появляется и не исчезает. Данные, заведённые до выключения, остаются на месте
    /// просто потому, что их никто не трогает (AUTH-19).
    /// </summary>
    [Fact]
    public async Task Disabled_module_neither_registers_services_nor_initializes()
    {
        var on = new CountingModule("id");
        var off = new CountingModule("costs");

        using var host = await StartAsync(enabled: "id", on, off);
        await host.Services.InitializeAppModulesAsync();

        Assert.Equal(1, on.ServicesRegistered);
        Assert.Equal(1, on.Initialized);
        Assert.Equal(0, off.ServicesRegistered);
        Assert.Equal(0, off.Initialized);
    }

    /// <summary>
    /// Повторный старт не задваивает инициализацию: она идёт по одному разу за запуск и только у
    /// включённых модулей. Сама идемпотентность содержимого — обязанность модуля (AUTH-20), и
    /// стеречь её будет первый модуль, которому есть что заводить.
    /// </summary>
    [Fact]
    public async Task Initialization_runs_once_per_start()
    {
        var module = new CountingModule("id");

        using var host = await StartAsync(enabled: "id", module);
        await host.Services.InitializeAppModulesAsync();
        Assert.Equal(1, module.Initialized);

        using var restarted = await StartAsync(enabled: "id", module);
        await restarted.Services.InitializeAppModulesAsync();
        Assert.Equal(2, module.Initialized);
    }

    /// <summary>
    /// Поднимает приложение с заданным набором модулей на тестовом сервере.
    /// </summary>
    private static async Task<IHost> StartAsync(
        string enabled, params IAppModule[] available) =>
        await StartAsync(enabled, configureCore: null, available);

    private static async Task<IHost> StartAsync(
        string enabled, Action<WebApplication>? configureCore, params IAppModule[] available)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Modules:Enabled"] = enabled });

        // Ворота группы включённого модуля требуют служб авторизации — без них приложение не
        // соберётся. Самим воротам здесь проверять нечего: тесты стучатся в адреса ядра и
        // выключенного модуля, а закрытость включённой группы стережёт ModuleBoundaryTests.
        builder.Services.AddAuthentication();
        builder.Services.AddAuthorization();
        builder.Services.AddAppModules(builder.Configuration, available);

        var app = builder.Build();
        configureCore?.Invoke(app);
        app.MapAppModules();
        await app.StartAsync();
        return app;
    }

    private sealed class IdLike()
        : TestModule("id", "Исполнительная документация", "/api/quality-docs");

    private sealed class CostsLike()
        : TestModule("costs", "Счета и накладные", "/api/costs/invoices");

    private sealed class SharedPrefixModule()
        : TestModule("costs", "Счета и накладные", "/api/document-sets/{setId}/print-form");

    /// <summary>Модуль с одним адресом: достаточно, чтобы проверить, чем отвечает экземпляр.</summary>
    private abstract class TestModule(string code, string title, string route) : IAppModule
    {
        public string Code => code;
        public string Title => title;
        public IReadOnlyList<string> Permissions => [];
        public virtual void RegisterServices(IServiceCollection services, IConfiguration configuration) { }
        public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
            endpoints.MapGet(route, () => Results.Ok("модуль отвечает"));
        public virtual Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Считает, сколько раз ядро обратилось к модулю.</summary>
    private sealed class CountingModule(string code)
        : TestModule(code, "проба " + code, "/api/" + code + "/probe")
    {
        public int ServicesRegistered { get; private set; }
        public int Initialized { get; private set; }

        public override void RegisterServices(IServiceCollection services, IConfiguration configuration) =>
            ServicesRegistered++;

        public override Task InitializeAsync(IServiceProvider services, CancellationToken ct)
        {
            Initialized++;
            return Task.CompletedTask;
        }
    }
}
