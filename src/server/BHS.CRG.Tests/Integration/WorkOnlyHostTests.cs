using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Documents;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Установка только с учётом работ: <c>Modules__Enabled=work</c>, без <c>id</c>, <c>costs</c> и <c>plan</c>
/// (change <c>work-10-skeleton</c>, ТЗ CORE-1, WORK-45, WORK-46). Проверка шага 2.1 зонтика
/// <c>work-00-epic</c>.
///
/// <para>Зачем ещё один живой хост, когда такие есть у счетов и смет (<see cref="CostsOnlyHostTests" />,
/// <see cref="PlanOnlyHostTests" />). Те доказали, что ядро не опирается на <c>id</c> и <c>costs</c>; этот —
/// что оно не опирается и на <c>plan</c>, хотя этап 3 успел дать ядру механизмы (право контракта,
/// межмодульный контракт, порт позиций перечня), первым потребителем которых был он. Учёт работ ими
/// пользуется — и обязан подняться там, где смет нет.</para>
///
/// <para>⚠️ Хост живёт на СВОЕЙ базе (<c>…_work</c>) по той же причине, что и соседи: состав системных ролей
/// приводится при старте к объявленному, и на общей тестовой базе этот хост снял бы у ролей права
/// остальных модулей.</para>
/// </summary>
[Collection(WorkOnlyCollection.Name)]
public class WorkOnlyHostTests(WorkOnlyHost host)
{
    /// <summary>
    /// Приложение поднимается без <c>id</c>, <c>costs</c> и <c>plan</c>, а их адреса отвечают отказом
    /// выключенного модуля с его названием (501, ТЗ OVW-10, AUTH-15, AUTH-19).
    /// </summary>
    [Fact]
    public async Task Host_starts_without_other_modules_and_their_addresses_refuse()
    {
        var client = host.CreateClient();

        var registry = host.Services.GetRequiredService<ModuleRegistry>();
        Assert.Equal([WorkModule.ModuleCode], registry.Enabled.Select(m => m.Code));
        Assert.Equal(["costs", "id", "plan"], registry.Disabled.Select(m => m.Code).Order(StringComparer.Ordinal));

        foreach (var (path, module) in new[]
                 {
                     ("/api/quality-docs", "id"), ("/api/costs/invoices", "costs"), ("/api/plan/estimates", "plan"),
                 })
        {
            var response = await client.GetAsync(path);

            Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(module, body.GetProperty("module").GetString());
        }
    }

    /// <summary>
    /// Включённый модуль отказом «модуля нет» на своём пути НЕ отвечает. Адресов у каркаса нет, поэтому
    /// ответ — пустой 404, а не 501: получив 501 от включённого модуля, человек пошёл бы включать то, что
    /// уже включено. Ворота группы проверит первый адрес под ними (<c>work-11</c>): путь без адреса
    /// отвечает 404 раньше авторизации.
    /// </summary>
    [Fact]
    public async Task Enabled_module_does_not_refuse_its_own_paths()
    {
        var response = await host.CreateClient().GetAsync("/api/work/assignments");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Схема модуля создана при старте — с нуля: хост сносит её до запуска (<see cref="WorkOnlyHost" />),
    /// поэтому схема и история с начальной миграцией могли появиться только от миграции модуля в этом
    /// старте. В истории ядра миграций модуля нет (ТЗ CORE-4). Порядок «ядро, затем модуль» — код
    /// <c>StartupTasks</c>; здесь видно, что до модуля дело дошло на экземпляре без трёх других.
    /// </summary>
    [Fact]
    public async Task Work_schema_is_migrated_with_its_own_history()
    {
        _ = host.CreateClient();

        using var scope = host.Services.CreateScope();
        var work = scope.ServiceProvider.GetRequiredService<WorkDbContext>();

        Assert.Contains("_work", work.Database.GetConnectionString() ?? string.Empty);
        Assert.True(host.SchemaDroppedBeforeStart, "Хост не снёс схему до старта — проверка шла бы вхолостую.");
        Assert.Empty(await work.Database.GetPendingMigrationsAsync());
        Assert.Contains(await work.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("_Initial", StringComparison.Ordinal));

        var core = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await core.Database.GetPendingMigrationsAsync());
        Assert.DoesNotContain(await core.Database.GetAppliedMigrationsAsync(),
            m => work.Database.GetMigrations().Contains(m));
    }

    /// <summary>
    /// Права модуля объявлены с объяснениями и доезжают до базы, а прав выключенных модулей в каталоге нет
    /// (AUTH-1, AUTH-19).
    /// </summary>
    [Fact]
    public async Task Work_permissions_are_declared_with_explanations_and_reach_the_database()
    {
        _ = host.CreateClient();

        var catalog = host.Services.GetRequiredService<PermissionCatalog>();
        var work = catalog.All.Where(p => p.Code.StartsWith("work.", StringComparison.Ordinal)).ToList();

        // Пять из восьми прав WORK-45 — правило этапа (D2 зонтика work-00-epic): право объявляется вместе с
        // первым адресом, который оно открывает. work.crew.approve — work-18, work.facts.read (право
        // контракта) — work-27, work.devices.manage — этап 5. Число названо нарочно: право, добавленное без
        // своего change, обязано столкнуться с этой строкой и с объяснением рядом с ней.
        Assert.Equal(5, work.Count);
        Assert.Equal(
            [WorkModule.Assign, WorkModule.ReportOwn, WorkModule.ReportRead, WorkModule.ReportReview, WorkModule.SitesAll],
            work.Select(p => p.Code).Order(StringComparer.Ordinal));
        Assert.All(work, p => Assert.Null(p.Validate()));
        Assert.All(work, p => Assert.False(p.Contract, $"«{p.Code}» — права контракта каркас не объявляет."));
        // В «читать всё» входит одно чтение отчётов; остальные пишут (AUTH-5.2, WORK-39).
        Assert.Equal([WorkModule.ReportRead], work.Where(p => p.ReadAll is { Included: true }).Select(p => p.Code));
        foreach (var prefix in new[] { "id.", "costs.", "plan." })
            Assert.DoesNotContain(catalog.Codes, c => c.StartsWith(prefix, StringComparison.Ordinal));

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Contains("_work", db.Database.GetConnectionString() ?? string.Empty);

        var stored = await db.Permissions.Where(p => p.Code.StartsWith("work.")).ToListAsync();
        foreach (var declared in work)
        {
            var row = stored.SingleOrDefault(p => p.Code == declared.Code);
            Assert.True(row is not null, $"Право «{declared.Code}» объявлено модулем, но в базу не попало.");
            Assert.True(row!.IsDeclared, $"Право «{declared.Code}» объявлено, но помечено как невыдаваемое.");
            Assert.Equal(declared.Gives, row.Gives);
        }
    }

    /// <summary>
    /// Права модуля либо стоят на двери, либо названы здесь как ещё не носящие ни одной (зеркало
    /// <see cref="EndpointGateInventoryTests" /> для модуля, выключенного на умолчательном хосте, AUTH-19).
    ///
    /// <para>Список — храповик: change, приносящий дверь, обязан убрать свою строку, иначе тест упадёт и
    /// потребует этого.</para>
    /// </summary>
    [Fact]
    public void Module_permissions_open_a_door_or_are_named_as_doorless()
    {
        _ = host.CreateClient();

        Dictionary<string, string> doorless = new()
        {
            [WorkModule.Assign] = "назначения на свои стройки — work-11",
            [WorkModule.SitesAll] = "назначения на любые стройки — work-11; приёмка на любых — work-16",
            [WorkModule.ReportOwn] = "свои отчёты — work-13",
            [WorkModule.ReportReview] = "отчёты своих строек и ввод за сотрудника — work-13",
            [WorkModule.ReportRead] = "чтение всех отчётов — work-13",
        };

        var catalog = host.Services.GetRequiredService<PermissionCatalog>();
        var used = EndpointInventory.GatingPermissions(EndpointInventory.Routes(host.Services));

        var silent = catalog.Codes
            .Where(c => c.StartsWith("work.", StringComparison.Ordinal))
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
    /// Состав <c>/api/account/access</c> по системным ролям — права <c>work.*</c> и признак модуля (ТЗ
    /// WORK-45, AUTH-14; D2 зонтика).
    ///
    /// <para>Объявление права молча меняет навигацию ролей: права, заранее вписанные в составы
    /// (<c>SystemRoles</c>), после объявления наполняют роли. Поэтому набор сверяется ТОЧНО, а не
    /// «содержит»: лишнее право здесь — пункт меню, которого роль не должна была получить.</para>
    ///
    /// <list type="bullet">
    ///   <item>«Администратор» — всё объявленное, в том числе <c>work.sites.all</c> («или администратор» из
    ///   WORK-21 выражено правом, WORK-46).</item>
    ///   <item>«Монтажник» — свои отчёты. До каркаса у него не было ни одного модуля.</item>
    ///   <item>«Менеджер проекта» — приёмка и назначения; <c>work.crew.approve</c> и
    ///   <c>work.devices.manage</c> в его составе ещё не объявлены и пропускаются.</item>
    ///   <item>«Руководитель» — чтение всех отчётов через «читать всё» (AUTH-5.2).</item>
    ///   <item>«Инженер ИД» и «Производитель работ» — модуля нет: у них в составе только
    ///   <c>work.facts.read</c>, право контракта, которое ещё не объявлено (<c>work-27</c>) и модуль не
    ///   откроет и после (AUTH-8.1).</item>
    /// </list>
    /// </summary>
    [Theory]
    [InlineData(SystemRoles.Admin, true,
        WorkModule.Assign, WorkModule.ReportOwn, WorkModule.ReportRead, WorkModule.ReportReview, WorkModule.SitesAll)]
    [InlineData("Installer", true, WorkModule.ReportOwn)]
    [InlineData("ProjectManager", true, WorkModule.Assign, WorkModule.ReportReview)]
    [InlineData("Executive", true, WorkModule.ReportRead)]
    [InlineData(SystemRoles.IdEngineer, false)]
    [InlineData("WorksManager", false)]
    [InlineData("Estimator", false)]
    [InlineData("Supplier", false)]
    [InlineData("Accountant", false)]
    public async Task System_roles_get_exactly_their_work_permissions(string role, bool opens, params string[] expected)
    {
        var access = await AccessProbe.AccessAsync(await SignInAsync(role));

        var work = access.Permissions.Where(c => c.StartsWith("work.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, work);

        var module = Assert.Single(access.Modules, m => m.Code == WorkModule.ModuleCode);
        Assert.Equal(opens, module.Available);
        Assert.Equal(new WorkModule().UsesSections, module.Sections);
    }

    /// <summary>
    /// «Справочник не заведён» ≠ «пусто» (<c>SkipWhenBlocked</c>, ТЗ WORK-47, CORE-8). На чистой установке
    /// только с учётом работ нет ни «Персоны», ни справочника единиц, и потому ни «Сотрудника», ни «Вида
    /// работы»: порты ядра, которыми модуль их читает, отвечают «не заведён» (<c>null</c>), а не пустым
    /// выбором, и слова для человека называют справочник. Дверь сотрудников ядра — 409 с причиной.
    ///
    /// <para>Адресов у каркаса нет — перевод <c>null</c> в отказ адреса модуля делает его первый потребитель
    /// (<c>work-11</c>); здесь проверено, что на экземпляре без <c>id</c> различать есть чем.</para>
    /// </summary>
    [Fact]
    public async Task Missing_directories_answer_not_set_up_rather_than_empty()
    {
        _ = host.CreateClient();

        using var scope = host.Services.CreateScope();
        var types = await scope.ServiceProvider.GetRequiredService<AppDbContext>().DocumentTypes
            .Select(t => t.Code).ToListAsync();
        // Предусловие обязано выполняться: с «Персоной» в базе проверка шла бы вхолостую.
        Assert.DoesNotContain(types, c => string.Equals(c, CoreRecordTypes.PersonCode, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(CoreRecordTypes.EmployeeCode, types);
        Assert.DoesNotContain(CoreRecordTypes.WorkTypeCode, types);

        var employees = scope.ServiceProvider.GetRequiredService<IModuleEmployees>();
        Assert.Null(await employees.SearchAsync(null, new DateOnly(2026, 11, 13), 10));
        Assert.Null(await employees.GetAsync([Guid.NewGuid()]));
        Assert.Contains("Справочник «Сотрудник» не заведён", IModuleEmployees.NotSetUp, StringComparison.Ordinal);

        var workTypes = scope.ServiceProvider.GetRequiredService<IModuleWorkTypes>();
        Assert.Null(await workTypes.SearchAsync(null, 10));
        Assert.Null(await workTypes.GetAsync([Guid.NewGuid()]));
        Assert.Contains("Справочник «Вид работы» не заведён", IModuleWorkTypes.NotSetUp, StringComparison.Ordinal);

        var response = await (await SignInAsync(SystemRoles.Admin)).GetAsync("/api/employees");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("directory-missing", body.GetProperty("code").GetString());
        Assert.Equal("person-missing", body.GetProperty("reason").GetString());
    }

    private Task<HttpClient> SignInAsync(string role) => AccessProbe.SignInAsync(host, role, "work");
}

/// <summary>Один хост «только учёт работ» на все классы, которым он нужен (как у счетов и смет).</summary>
[CollectionDefinition(Name)]
public sealed class WorkOnlyCollection : ICollectionFixture<WorkOnlyHost>
{
    public const string Name = "Хост «только учёт работ»";
}

/// <summary>
/// Хост «только учёт работ»: <c>Modules__Enabled=work</c> и своя база «…_work» (как он держится —
/// <see cref="ModuleOnlyHost" />).
///
/// <para>⚠️ Схема <c>work</c> СНОСИТСЯ до запуска, как у <see cref="PlanOnlyHost" />: без этого проверка
/// «схема мигрирована при старте» проходила бы на схеме, оставшейся от прошлого прогона, — и с вырезанной
/// миграцией модуля тоже. Снос — после того, как база занята (<see cref="TestRunDatabase.ClaimAsync" />):
/// чужой прогон на ней же обязан получить отказ раньше первой правки.</para>
/// </summary>
public sealed class WorkOnlyHost : ModuleOnlyHost
{
    protected override string ModuleCode => WorkModule.ModuleCode;

    protected override string DatabaseSuffix => "work";

    /// <summary>Снос схемы до старта действительно был — тест опирается на него и проверяет это.</summary>
    internal bool SchemaDroppedBeforeStart { get; }

    public WorkOnlyHost()
    {
        // Task.Run — чтобы ожидание в конструкторе не зависело от контекста синхронизации xUnit.
        Task.Run(() => TestRunDatabase.ClaimAsync(ConnectionString, ClaimPatience)).GetAwaiter().GetResult();

        using var conn = new NpgsqlConnection(ConnectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {WorkDbContext.SchemaName} CASCADE", conn);
        cmd.ExecuteNonQuery();
        SchemaDroppedBeforeStart = true;
    }
}
