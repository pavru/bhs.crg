using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Установка только с планированием: <c>Modules__Enabled=plan</c>, без <c>id</c> и <c>costs</c>
/// (change <c>plan-01-skeleton</c>, ТЗ CORE-1, PLAN-23).
///
/// <para>Зачем живой хост, когда такой же есть у счетов (<see cref="CostsOnlyHostTests" />). Тот
/// доказал, что ядро не опирается на <c>id</c>; этот — что оно не опирается и на <c>costs</c>, то есть
/// на модуль, который за этап 2 успел стать «вторым по умолчанию». Порты, которые ядро с тех пор
/// приобрело для счетов, и проекции их типов обязаны пережить экземпляр, где счетов нет.</para>
///
/// <para>⚠️ Хост живёт на СВОЕЙ базе (<c>…_plan</c>) по той же причине, что и хост счетов: состав
/// системных ролей приводится при старте к объявленному, и на общей тестовой базе этот хост снял бы у
/// ролей все права <c>id.*</c> и <c>costs.*</c>.</para>
/// </summary>
[Collection(PlanOnlyCollection.Name)]
public class PlanOnlyHostTests(PlanOnlyHost host)
{
    private const string Password = "Passw0rd!";

    /// <summary>
    /// Приложение поднимается без <c>id</c> и <c>costs</c>, а их адреса отвечают отказом выключенного
    /// модуля с его названием (501, ТЗ OVW-10, AUTH-15, AUTH-19).
    /// </summary>
    [Fact]
    public async Task Host_starts_without_id_and_costs_and_their_addresses_refuse()
    {
        var client = host.CreateClient();

        var registry = host.Services.GetRequiredService<ModuleRegistry>();
        Assert.Equal(["plan"], registry.Enabled.Select(m => m.Code));
        Assert.Contains("id", registry.Disabled.Select(m => m.Code));
        Assert.Contains("costs", registry.Disabled.Select(m => m.Code));

        foreach (var (path, module) in new[] { ("/api/quality-docs", "id"), ("/api/costs/invoices", "costs") })
        {
            var response = await client.GetAsync(path);

            Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(module, body.GetProperty("module").GetString());
        }
    }

    /// <summary>
    /// Включённый модуль отказом «модуля нет» на своём пути НЕ отвечает: адресов у каркаса нет, и ответ —
    /// обычный 404, а не 501. Получив 501 от включённого модуля, человек пошёл бы включать то, что уже
    /// включено. Ждёт 404 ровно до первого адреса (<c>plan-04</c>), как ждал хост счетов до C1.
    /// </summary>
    [Fact]
    public async Task Enabled_module_does_not_refuse_its_own_paths()
    {
        var response = await host.CreateClient().GetAsync("/api/plan/estimates");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Схема модуля мигрирована при старте — в ней своя история с начальной миграцией, — а в истории ядра
    /// миграций модуля нет (ТЗ CORE-4). Порядок «ядро, затем модуль» задаёт <c>StartupTasks</c>; здесь
    /// видно, что до модуля дело дошло на базе, где нет ни <c>id</c>, ни <c>costs</c>.
    /// </summary>
    [Fact]
    public async Task Plan_schema_is_migrated_with_its_own_history()
    {
        _ = host.CreateClient();

        using var scope = host.Services.CreateScope();
        var plan = scope.ServiceProvider.GetRequiredService<PlanDbContext>();

        Assert.Contains("_plan", plan.Database.GetConnectionString() ?? string.Empty);
        Assert.Empty(await plan.Database.GetPendingMigrationsAsync());
        Assert.Contains(await plan.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("_Initial", StringComparison.Ordinal));

        var core = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.DoesNotContain(await core.Database.GetAppliedMigrationsAsync(),
            m => plan.Database.GetMigrations().Contains(m));
    }

    /// <summary>
    /// Права модуля объявлены с объяснениями и доезжают до базы, а прав выключенных модулей в каталоге нет
    /// (AUTH-1, AUTH-19).
    /// </summary>
    [Fact]
    public async Task Plan_permissions_are_declared_with_explanations_and_reach_the_database()
    {
        _ = host.CreateClient();

        var catalog = host.Services.GetRequiredService<PermissionCatalog>();
        var plan = catalog.All.Where(p => p.Code.StartsWith("plan.", StringComparison.Ordinal)).ToList();

        // Два из двенадцати прав PLAN-23 — правило этапа (D12 зонтика plan-00-epic): право объявляется
        // вместе с первым адресом, который оно открывает; сметы — первая дверь модуля. Остальные приходят
        // своими change (cost — 06, offer — 13, pricelist — 14, календарь и графики — 18, права контракта
        // — 23). Число названо здесь нарочно: право, добавленное без своего change, обязано столкнуться с
        // этой строкой и с объяснением рядом с ней.
        Assert.Equal(2, plan.Count);
        Assert.Equal([PlanModule.EstimateEdit, PlanModule.EstimateRead], plan.Select(p => p.Code).Order(StringComparer.Ordinal));
        Assert.All(plan, p => Assert.Null(p.Validate()));
        Assert.DoesNotContain(catalog.Codes, c => c.StartsWith("id.", StringComparison.Ordinal));
        Assert.DoesNotContain(catalog.Codes, c => c.StartsWith("costs.", StringComparison.Ordinal));

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Contains("_plan", db.Database.GetConnectionString() ?? string.Empty);

        var stored = await db.Permissions.Where(p => p.Code.StartsWith("plan.")).ToListAsync();
        foreach (var declared in plan)
        {
            var row = stored.SingleOrDefault(p => p.Code == declared.Code);
            Assert.True(row is not null, $"Право «{declared.Code}» объявлено модулем, но в базу не попало.");
            Assert.True(row!.IsDeclared, $"Право «{declared.Code}» объявлено, но помечено как невыдаваемое.");
            Assert.Equal(declared.Gives, row.Gives);
        }
    }

    /// <summary>
    /// Права модуля либо стоят на двери, либо названы здесь как ещё не носящие ни одной (зеркало
    /// <see cref="EndpointGateInventoryTests" /> для модуля, выключенного на умолчательном хосте — тот
    /// прав <c>plan</c> не видит вовсе, AUTH-19).
    ///
    /// <para>Список — храповик: задача, приносящая дверь, обязана убрать свою строку, иначе тест упадёт
    /// и потребует этого.</para>
    /// </summary>
    [Fact]
    public void Module_permissions_open_a_door_or_are_named_as_doorless()
    {
        _ = host.CreateClient();

        Dictionary<string, string> doorless = new()
        {
            // Объявлены в каркасе ради составов ролей (PLAN-23), двери приходят с импортом сметы.
            [PlanModule.EstimateRead] = "plan-04: импорт и чтение сметы",
            [PlanModule.EstimateEdit] = "plan-04: импорт сметы",
        };

        var catalog = host.Services.GetRequiredService<PermissionCatalog>();
        var used = EndpointInventory.GatingPermissions(EndpointInventory.Routes(host.Services));

        var silent = catalog.Codes
            .Where(c => c.StartsWith("plan.", StringComparison.Ordinal))
            .Where(c => !used.Contains(c) && !doorless.ContainsKey(c))
            .Order(StringComparer.Ordinal).ToList();

        Assert.True(silent.Count == 0,
            "Право модуля объявлено, но не стоит ни на одном адресе. Галка в редакторе ролей будет " +
            "выдаваться и не делать ничего. Поставьте право на дверь либо назовите здесь задачу, " +
            "которая её принесёт:\n  " + string.Join("\n  ", silent));

        var stale = doorless.Keys.Where(used.Contains).Order(StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0,
            "У этих прав двери уже есть — уберите записи:\n  " + string.Join("\n  ", stale));

        var unknown = doorless.Keys.Where(c => !catalog.Declares(c)).Order(StringComparer.Ordinal).ToList();
        Assert.True(unknown.Count == 0,
            "В списке есть коды, которых модуль не объявляет:\n  " + string.Join("\n  ", unknown));
    }

    /// <summary>
    /// Состав <c>/api/account/access</c> по системным ролям — права <c>plan.*</c> и признак модуля
    /// (ТЗ PLAN-2, PLAN-23, AUTH-14).
    ///
    /// <para>Объявление права молча меняет навигацию ролей: права, заранее вписанные в составы
    /// (<c>SystemRoles</c>), после объявления наполняют роли. Поэтому набор сверяется ТОЧНО, а не
    /// «содержит»: лишнее право здесь — пункт меню, которого роль не должна была получить.</para>
    ///
    /// <list type="bullet">
    ///   <item>«Сметчик» — сметы; ни одного <c>plan.offer.*</c>: без права на КП он не видит цен продажи
    ///   (PLAN-2). Себестоимость (<c>plan.estimate.cost</c>) придёт к нему с <c>plan-06</c>.</item>
    ///   <item>«Менеджер проекта» — только чтение смет (решение В-1, PLAN-Q9): КП, прайс-лист и графики
    ///   объявят свои change.</item>
    ///   <item>«Руководитель» — чтение смет через «читать всё»; правка в него не входит.</item>
    ///   <item>«Инженер ИД», «Бухгалтер», «Монтажник», «Снабженец» — модуля нет. У монтажника и
    ///   снабженца в составе стоят права контракта (<c>plan.estimate.hint</c>,
    ///   <c>plan.estimate.materials</c>) — они ещё не объявлены и пропускаются; модуль им не откроют и
    ///   после объявления (AUTH-8.1, <c>plan-21</c>).</item>
    /// </list>
    ///
    /// <para>⚠️ Что ворота <c>/api/plan</c> закрыты ролям без прав модуля, здесь НЕ проверяется: у пустой
    /// группы адресов нет, и путь отвечает 404 раньше авторизации — любой роли. Доказывает ворота первый
    /// адрес под ними (<c>plan-04</c>), и проверяется это там же; признак модуля и ворота считаются
    /// одним правилом (<c>ModuleAccess</c>), это сверяет <c>AccountAccessTests.Module_flag_matches_the_module_gate</c>.</para>
    /// </summary>
    [Theory]
    [InlineData("Estimator", true, PlanModule.EstimateEdit, PlanModule.EstimateRead)]
    [InlineData("ProjectManager", true, PlanModule.EstimateRead)]
    [InlineData("Executive", true, PlanModule.EstimateRead)]
    [InlineData(SystemRoles.IdEngineer, false)]
    [InlineData("Accountant", false)]
    [InlineData("Installer", false)]
    [InlineData("Supplier", false)]
    [InlineData("WorksManager", false)]
    public async Task System_roles_get_exactly_their_plan_permissions(string role, bool opens, params string[] expected)
    {
        var access = await AccessAsync(await SignInAsync(role));

        var plan = access.Permissions.Where(c => c.StartsWith("plan.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
        // Точное равенство — оно же и «у сметчика ни одного plan.offer.*»: лишний код здесь не прошёл бы.
        Assert.Equal(expected, plan);

        var module = Assert.Single(access.Modules, m => m.Code == PlanModule.ModuleCode);
        Assert.Equal(opens, module.Available);
        Assert.Equal(new PlanModule().UsesSections, module.Sections);
    }

    private sealed record ModuleDto(string Code, string Title, bool Available, string[] Sections);
    private sealed record AccessDto(string[] Permissions, ModuleDto[] Modules);

    private static async Task<AccessDto> AccessAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/account/access");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccessDto>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }

    private async Task<HttpClient> SignInAsync(string role)
    {
        var email = $"plan_{role.ToLowerInvariant()}_{Guid.NewGuid():N}@test.local";
        using (var scope = host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                UserName = email, Email = email, DisplayName = "Тест", EmailConfirmed = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        }

        var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

/// <summary>Один хост «только планирование» на все классы, которым он нужен (как у счетов, issue #1077).</summary>
[CollectionDefinition(Name)]
public sealed class PlanOnlyCollection : ICollectionFixture<PlanOnlyHost>
{
    public const string Name = "Хост «только планирование»";
}

/// <summary>
/// Хост «только планирование»: <c>Modules__Enabled=plan</c> и отдельная база. Устроен как
/// <see cref="CostsOnlyHost" />: переопределения добавляются ПОСЛЕ базовых — последний слой
/// конфигурации выигрывает.
/// </summary>
public sealed class PlanOnlyHost : IntegrationTestFixture
{
    /// <summary>Своя база — «…_plan» рядом с общей тестовой; создаётся сама миграцией при старте.</summary>
    private static string ConnectionString { get; } = TestDatabases.ConnectionString("plan");

    protected override string HostConnectionString => ConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        var overrides = new Dictionary<string, string?>
        {
            ["Modules:Enabled"] = "plan",
            ["ConnectionStrings:Postgres"] = ConnectionString,
        };

        foreach (var (key, value) in overrides) builder.UseSetting(key, value);
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(overrides));
    }
}
