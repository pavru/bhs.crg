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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
/// остальных модулей. И стартует он как ЧИСТАЯ установка — ядро тоже с нуля (<see cref="WorkOnlyHost" />).</para>
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

        // Каждый — Contains, а не точный список выключенных: модуль, который появится в сборке позже, к
        // учёту работ отношения не имеет и ронять этот тест не должен.
        foreach (var (path, module) in new[]
                 {
                     ("/api/quality-docs", "id"), ("/api/costs/invoices", "costs"), ("/api/plan/estimates", "plan"),
                 })
        {
            Assert.Contains(module, registry.Disabled.Select(m => m.Code));

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
    /// Миграции на чистой установке: хост сносит схемы ядра и модуля до запуска (<see cref="WorkOnlyHost" />),
    /// поэтому история ядра и история модуля с начальной миграцией могли появиться только в этом старте. В
    /// истории ядра миграций модуля нет (ТЗ CORE-4). Порядок «ядро, затем модуль» — код <c>StartupTasks</c>;
    /// здесь видно, что на экземпляре без трёх других модулей до модуля дело дошло.
    /// </summary>
    [Fact]
    public async Task Work_schema_is_migrated_with_its_own_history()
    {
        _ = host.CreateClient();

        using var scope = host.Services.CreateScope();
        var work = scope.ServiceProvider.GetRequiredService<WorkDbContext>();

        Assert.Contains("_work", work.Database.GetConnectionString() ?? string.Empty);
        Assert.True(host.SchemaDroppedBeforeStart, "Хост не снёс схемы до старта — проверка шла бы вхолостую.");
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
        // Пять из восьми прав WORK-45 — правило этапа (D2 зонтика work-00-epic): право объявляется вместе с
        // первым адресом, который оно открывает. work.crew.approve — work-18, work.facts.read (право
        // контракта) — work-27, work.devices.manage — этап 5. Список назван нарочно: право, добавленное без
        // своего change, обязано столкнуться с этой строкой и с объяснением рядом с ней.
        var work = await ModuleOnlyChecks.DeclaredAndStoredAsync(host, WorkModule.ModuleCode,
            [WorkModule.Assign, WorkModule.ReportOwn, WorkModule.ReportRead, WorkModule.ReportReview, WorkModule.SitesAll],
            "id", "costs", "plan");

        Assert.All(work, p => Assert.False(p.Contract, $"«{p.Code}» — права контракта каркас не объявляет."));
        // В «читать всё» входит одно чтение отчётов; остальные пишут (AUTH-5.2, WORK-39).
        Assert.Equal([WorkModule.ReportRead], work.Where(p => p.ReadAll is { Included: true }).Select(p => p.Code));
    }

    /// <summary>
    /// Права модуля либо стоят на двери, либо названы здесь с причиной (храповик: запись права, у которого
    /// дверь появилась, обязана уйти).
    /// </summary>
    [Fact]
    public void Module_permissions_open_a_door_or_are_named_as_doorless() =>
        ModuleOnlyChecks.DoorOrNamedDoorless(host, WorkModule.ModuleCode, new Dictionary<string, string>
        {
            [WorkModule.Assign] = "назначения на свои стройки — work-11",
            [WorkModule.ReportOwn] = "свои отчёты — work-13",
            [WorkModule.ReportReview] = "отчёты своих строек и ввод за сотрудника — work-13",
            [WorkModule.ReportRead] = "чтение всех отчётов — work-13",
            // Не дверь и не станет ею (ревью PR #1932): право снимает границу «свои стройки» ВНУТРИ адресов
            // назначений и приёмки — его спрашивает сам адрес (IModuleUser.HasAsync, design work-11), как
            // себестоимость сметы у plan. Запись постоянная.
            [WorkModule.SitesAll] =
                "не дверь, а граница внутри адресов назначений (work-11) и приёмки (work-16): «на любых стройках»",
        });

    /// <summary>
    /// Состав <c>/api/account/access</c> по системным ролям — права <c>work.*</c> и признак модуля (ТЗ
    /// WORK-45, AUTH-14; D2 зонтика). Объявление права молча меняет навигацию ролей: права, заранее
    /// вписанные в составы (<c>SystemRoles</c>), после объявления наполняют роли. Поэтому набор сверяется
    /// ТОЧНО.
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
    public async Task System_roles_get_exactly_their_work_permissions(string role, bool opens, params string[] expected) =>
        await ModuleOnlyChecks.RoleGetsExactlyAsync(host, new WorkModule(), role, opens, expected);

    /// <summary>
    /// «Справочник не заведён» ≠ «пусто» (<c>SkipWhenBlocked</c>, ТЗ WORK-47, CORE-8). На чистой установке
    /// только с учётом работ нет ни «Персоны», ни справочника единиц, и потому ни «Сотрудника», ни «Вида
    /// работы»: порты ядра, которыми модуль их читает, отвечают «не заведён» (<c>null</c>), а не пустым
    /// выбором, и слова для человека называют справочник. Дверь сотрудников ядра — 409 с причиной.
    ///
    /// <para>Адресов у каркаса нет — перевод <c>null</c> в отказ адреса модуля делает его первый потребитель
    /// (<c>work-11</c>); здесь проверено, что на экземпляре без <c>id</c> различать есть чем.</para>
    ///
    /// <para>⚠️ Предусловие держит снос ядра до старта (<see cref="WorkOnlyHost" />): между прогонами типы не
    /// копятся. Внутри прогона его держит правило коллекции — классы, которые заводят справочники, живут на
    /// своём хосте, а не на этом.</para>
    /// </summary>
    [Fact]
    public async Task Missing_directories_answer_not_set_up_rather_than_empty()
    {
        _ = host.CreateClient();

        using var scope = host.Services.CreateScope();
        var types = await scope.ServiceProvider.GetRequiredService<AppDbContext>().DocumentTypes
            .Select(t => t.Code).ToListAsync();
        // Предусловие обязано выполняться: с «Персоной» в базе проверка шла бы вхолостую.
        foreach (var code in new[] { CoreRecordTypes.PersonCode, CoreRecordTypes.EmployeeCode, CoreRecordTypes.WorkTypeCode })
            Assert.DoesNotContain(types, c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase));

        var employees = scope.ServiceProvider.GetRequiredService<IModuleEmployees>();
        Assert.Null(await employees.SearchAsync(null, new DateOnly(2026, 11, 13), 10));
        Assert.Null(await employees.GetAsync([Guid.NewGuid()]));
        Assert.Contains("Справочник «Сотрудник» не заведён", IModuleEmployees.NotSetUp, StringComparison.Ordinal);

        var workTypes = scope.ServiceProvider.GetRequiredService<IModuleWorkTypes>();
        Assert.Null(await workTypes.SearchAsync(null, 10));
        Assert.Null(await workTypes.GetAsync([Guid.NewGuid()]));
        Assert.Contains("Справочник «Вид работы» не заведён", IModuleWorkTypes.NotSetUp, StringComparison.Ordinal);

        var response = await (await AccessProbe.SignInAsync(host, SystemRoles.Admin, "work")).GetAsync("/api/employees");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("directory-missing", body.GetProperty("code").GetString());
        Assert.Equal("person-missing", body.GetProperty("reason").GetString());
    }
}

/// <summary>
/// Один хост «только учёт работ» на все классы, которым он нужен (как у счетов и смет).
///
/// <para>⚠️ Класс, который заводит справочники ядра («Персону», единицы), сюда не входит: этот хост —
/// чистая установка, и на ней проверяется «справочник не заведён».</para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class WorkOnlyCollection : ICollectionFixture<WorkOnlyHost>
{
    public const string Name = "Хост «только учёт работ»";
}

/// <summary>
/// Хост «только учёт работ»: <c>Modules__Enabled=work</c> и своя база «…_work» (как он держится —
/// <see cref="ModuleOnlyHost" />).
///
/// <para>⚠️ До запуска СНОСЯТСЯ и схема <c>work</c>, и схема ядра (ревью PR #1932): хост стартует как
/// чистая установка. Без сноса ядра база хоста копила бы типы между прогонами, и проверка «справочник не
/// заведён» зависела бы от того, что осталось с прошлого раза; заодно каждый прогон проверяет порядок
/// «миграции ядра с нуля, затем модуль» (задача 2.2 change). Стоит секунды: ядро мигрирует с пустой базы.</para>
/// </summary>
public sealed class WorkOnlyHost : ModuleOnlyHost
{
    protected override string ModuleCode => WorkModule.ModuleCode;

    protected override string DatabaseSuffix => "work";

    protected override IReadOnlyList<string> SchemasDroppedBeforeStart => [WorkDbContext.SchemaName, "public"];
}
