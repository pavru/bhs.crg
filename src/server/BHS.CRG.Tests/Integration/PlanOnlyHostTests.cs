using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

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
    /// Включённый модуль отказом «модуля нет» на своём пути НЕ отвечает: адрес смет есть (<c>plan-06</c>),
    /// и без входа ответ — 401, а не 501. Получив 501 от включённого модуля, человек пошёл бы включать то,
    /// что уже включено.
    /// </summary>
    [Fact]
    public async Task Enabled_module_does_not_refuse_its_own_paths()
    {
        var response = await host.CreateClient().GetAsync("/api/plan/estimates");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Схема модуля создана при старте — с нуля: хост сносит её до запуска (<see cref="PlanOnlyHost" />),
    /// поэтому схема и история с начальной миграцией могли появиться только от миграции модуля в этом
    /// старте, а не остаться от прошлого прогона. В истории ядра миграций модуля нет (ТЗ CORE-4).
    ///
    /// <para>Порядок «ядро, затем модуль» — код <c>StartupTasks</c>: миграции модулей идут после
    /// миграции ядра. Здесь видно, что до модуля дело дошло на экземпляре без <c>id</c> и <c>costs</c>;
    /// на пустой базе CI проходят обе.</para>
    /// </summary>
    [Fact]
    public async Task Plan_schema_is_migrated_with_its_own_history()
    {
        _ = host.CreateClient();

        using var scope = host.Services.CreateScope();
        var plan = scope.ServiceProvider.GetRequiredService<PlanDbContext>();

        Assert.Contains("_plan", plan.Database.GetConnectionString() ?? string.Empty);
        Assert.True(host.SchemaDroppedBeforeStart, "Хост не снёс схему до старта — проверка шла бы вхолостую.");
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

        // Три из двенадцати прав PLAN-23 — правило этапа (D12 зонтика plan-00-epic): право объявляется
        // вместе с первым адресом, который оно открывает; сметы — первая дверь модуля, себестоимость
        // закрывает модель сметы (06). Остальные приходят своими change (offer — 13, pricelist — 14,
        // календарь и графики — 18, права контракта — 23). Число названо здесь нарочно: право, добавленное
        // без своего change, обязано столкнуться с этой строкой и с объяснением рядом с ней.
        Assert.Equal(3, plan.Count);
        Assert.Equal([PlanModule.EstimateCost, PlanModule.EstimateEdit, PlanModule.EstimateRead],
            plan.Select(p => p.Code).Order(StringComparer.Ordinal));
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

        // Пусто: двери смет пришли с моделью сметы (plan-06). Себестоимость (plan.estimate.cost) двери не
        // открывает — она открывает часть ответа, — поэтому на воротах её нет и здесь она названа ниже.
        Dictionary<string, string> doorless = new()
        {
            [PlanModule.EstimateCost] =
                "не дверь, а часть ответа: без права ответы смет и строк идут без денег (plan-06, PLAN-11)",
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
    ///   <item>«Администратор» — всё объявленное, на экземпляре с <c>plan</c> — и права <c>plan</c>.</item>
    ///   <item>«Сметчик» — сметы с себестоимостью (<c>plan.estimate.cost</c>, <c>plan-06</c>); ни одного
    ///   <c>plan.offer.*</c>: без права на КП он не видит цен продажи (PLAN-2).</item>
    ///   <item>«Менеджер проекта» — только чтение смет, БЕЗ себестоимости (решение В-1, PLAN-Q9): КП,
    ///   прайс-лист и графики объявят свои change.</item>
    ///   <item>«Руководитель» — чтение смет с деньгами через «читать всё» (D12 зонтика); правка в него не
    ///   входит.</item>
    ///   <item>«Инженер ИД», «Бухгалтер», «Монтажник», «Снабженец» — модуля нет. У монтажника и
    ///   снабженца в составе стоят права контракта (<c>plan.estimate.hint</c>,
    ///   <c>plan.estimate.materials</c>) — они ещё не объявлены и пропускаются; модуль им не откроют и
    ///   после объявления (AUTH-8.1, <c>plan-21</c>).</item>
    /// </list>
    ///
    /// <para>Что ворота <c>/api/plan</c> закрыты ролям без прав модуля, проверяет первый адрес под ними —
    /// сметы (<c>EstimateEndpointsTests</c>, <c>plan-06</c>); признак модуля и ворота считаются одним
    /// правилом (<c>ModuleAccess</c>), это сверяет <c>AccountAccessTests.Module_flag_matches_the_module_gate</c>.</para>
    /// </summary>
    [Theory]
    [InlineData(SystemRoles.Admin, true, PlanModule.EstimateCost, PlanModule.EstimateEdit, PlanModule.EstimateRead)]
    [InlineData("Estimator", true, PlanModule.EstimateCost, PlanModule.EstimateEdit, PlanModule.EstimateRead)]
    [InlineData("ProjectManager", true, PlanModule.EstimateRead)]
    [InlineData("Executive", true, PlanModule.EstimateCost, PlanModule.EstimateRead)]
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

    /// <summary>
    /// Права КОНТРАКТА модуль не открывают (AUTH-8.1, D11 зонтика <c>plan-00-epic</c>): роль, у которой
    /// из прав <c>plan</c> в составе только они, не получает ни признака модуля, ни его ворот.
    ///
    /// <para>Роли отбираются по СОСТАВУ в <c>SystemRoles</c>, а не по ответу доступа, — нарочно (ревью
    /// PR #1580). Сегодня права контракта ещё не объявлены, и «Монтажник» со «Снабженцем» модуля не
    /// получают просто потому, что получать нечего. Объявит их <c>plan-23</c>; признак права контракта,
    /// без которого любое <c>plan.*</c> открывает модуль (<c>ModuleAccess.IsOpen</c>), строит <c>plan-21</c>.
    /// Если порядок перепутают, этот тест покраснеет сам — не дожидаясь, пока кто-то поправит ожидания в
    /// таблице ролей выше.</para>
    /// </summary>
    [Fact]
    public async Task Contract_rights_alone_do_not_open_the_module()
    {
        string[] contract = ["plan.estimate.materials", "plan.labor.read", "plan.estimate.hint"];

        var contractOnly = SystemRoles.All
            .Where(r => !r.AllPermissions)
            .Where(r => r.Permissions.Any(c => c.StartsWith("plan.", StringComparison.Ordinal)))
            .Where(r => r.Permissions.Where(c => c.StartsWith("plan.", StringComparison.Ordinal)).All(contract.Contains))
            .Select(r => r.Name)
            .ToList();

        // Проверка обязана до кого-то доходить: пустой отбор сделал бы тест зелёным навсегда.
        Assert.Equal(["Installer", "Supplier"], contractOnly.Order(StringComparer.Ordinal));

        foreach (var role in contractOnly)
        {
            var module = Assert.Single((await AccessAsync(await SignInAsync(role))).Modules,
                m => m.Code == PlanModule.ModuleCode);
            Assert.False(module.Available,
                $"Роль «{role}» получила модуль plan, имея из его прав только права контракта. Признак права " +
                "контракта (plan-21) обязан появиться раньше, чем права контракта объявят (plan-23).");
        }
    }

    private static Task<AccessProbe.AccessDto> AccessAsync(HttpClient client) => AccessProbe.AccessAsync(client);

    private Task<HttpClient> SignInAsync(string role) => AccessProbe.SignInAsync(host, role, "plan");
}

/// <summary>Один хост «только планирование» на все классы, которым он нужен (как у счетов, issue #1077).</summary>
[CollectionDefinition(Name)]
public sealed class PlanOnlyCollection : ICollectionFixture<PlanOnlyHost>
{
    public const string Name = "Хост «только планирование»";
}

/// <summary>
/// Хост «только планирование»: <c>Modules__Enabled=plan</c> и своя база «…_plan» (как он держится —
/// <see cref="ModuleOnlyHost" />).
///
/// <para>⚠️ Схема <c>plan</c> СНОСИТСЯ до запуска (ревью PR #1580). Без этого проверка «схема
/// мигрирована при старте» проходила бы на схеме, оставшейся от прошлого прогона, — и с вырезанной
/// миграцией модуля тоже. Так же устроен <see cref="ModuleSchemaHost" /> для <c>costs</c>. Снос — после
/// того, как база занята (<see cref="TestRunDatabase.ClaimAsync" />): чужой прогон на ней же обязан
/// получить отказ раньше первой правки. Заодно база создаётся, если её нет.</para>
/// </summary>
public sealed class PlanOnlyHost : ModuleOnlyHost
{
    protected override string ModuleCode => PlanModule.ModuleCode;

    protected override string DatabaseSuffix => "plan";

    /// <summary>Снос схемы до старта действительно был — тест опирается на него и проверяет это.</summary>
    internal bool SchemaDroppedBeforeStart { get; }

    public PlanOnlyHost()
    {
        // Task.Run — чтобы ожидание в конструкторе не зависело от контекста синхронизации xUnit.
        Task.Run(() => TestRunDatabase.ClaimAsync(ConnectionString, ClaimPatience)).GetAwaiter().GetResult();

        using var conn = new NpgsqlConnection(ConnectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {PlanDbContext.SchemaName} CASCADE", conn);
        cmd.ExecuteNonQuery();
        SchemaDroppedBeforeStart = true;
    }
}
