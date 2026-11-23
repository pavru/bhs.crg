using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Plan;
using BHS.CRG.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
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
        // И обратное направление к WorkOnlyHostTests (work-10): смета не опирается на учёт работ.
        Assert.Contains("work", registry.Disabled.Select(m => m.Code));

        foreach (var (path, module) in new[] { ("/api/quality-docs", "id"), ("/api/costs/invoices", "costs"), ("/api/work/assignments", "work"),
                     ("/api/work/reports", "work"), ("/api/work/entry/reports", "work"), ("/api/work/review/queue", "work"),
                     ($"/api/work/photos/{Guid.NewGuid()}", "work") })
        {
            var response = await client.GetAsync(path);

            Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(module, body.GetProperty("module").GetString());
        }

        // Пишущий адрес приёмки (work-16) — тот же отказ, а не 404: модуль назван, и человек знает, что включать.
        var accept = await client.PostAsync($"/api/work/reports/{Guid.NewGuid()}/accept", null);
        Assert.Equal(HttpStatusCode.NotImplemented, accept.StatusCode);
        Assert.Equal("work", (await accept.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("module").GetString());
    }

    /// <summary>
    /// Контракт «Фактические затраты» на экземпляре без счетов — «не подключено» с названием поставщика (change
    /// <c>plan-24-actual-costs-contract</c>, ТЗ CORE-34, OVW-9): «Ход работ» обязан сказать, чего не хватает, а не
    /// показать нулевые затраты.
    /// </summary>
    [Fact]
    public void Фактические_затраты_без_счетов_не_подключены()
    {
        _ = host.CreateClient();
        using var scope = host.Services.CreateScope();

        var answer = Assert.IsType<ModuleContractAnswer<IActualCosts>.NotConnected>(
            scope.ServiceProvider.GetRequiredService<IModuleContracts>().Get<IActualCosts>());

        Assert.Equal("costs", answer.Provider);
        Assert.Equal("не подключено: модуль «Счета и накладные»", answer.Text);
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

    [Fact]
    public async Task Offer_template_can_be_opened_and_saved_without_id()
    {
        await EstimateSeed.ClassifierAsync(host);
        var editor = await GrantedSignIn.WithPermissionsAsync(host, CorePermissions.PrintEdit);
        var types = await editor.GetFromJsonAsync<JsonElement>("/api/templates/types");
        var option = Assert.Single(types.EnumerateArray());
        var type = option.GetProperty("type");
        Assert.Equal(PlanRecordTypes.OfferCode, type.GetProperty("code").GetString());
        var requisites = option.GetProperty("printRequisites");
        Assert.Equal("conditions", requisites.GetProperty("customFieldsKey").GetString());
        Assert.Contains(requisites.GetProperty("fields").EnumerateArray(),
            field => field.GetProperty("path").EnumerateArray().Select(p => p.GetString()).SequenceEqual(["money", "total"]));
        var typeId = type.GetProperty("id").GetGuid();
        var active = await editor.GetFromJsonAsync<JsonElement>($"/api/templates/active?documentTypeId={typeId}");
        Assert.True(active.GetProperty("isDefault").GetBoolean());
        var templateId = active.GetProperty("id").GetGuid();
        var sourceUrl = $"/api/templates/{templateId}/content";
        var content = (await editor.GetFromJsonAsync<JsonElement>(sourceUrl)).GetProperty("content").GetString();
        Assert.Contains("Коммерческое предложение", content);
        (await editor.PutAsJsonAsync(sourceUrl, new { content })).EnsureSuccessStatusCode();
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
    public async Task Plan_permissions_are_declared_with_explanations_and_reach_the_database() =>
        // Десять из двенадцати прав PLAN-23 — правило этапа (D12 зонтика plan-00-epic): право объявляется
        // вместе с первым адресом или контрактом, который оно открывает; сметы — первая дверь модуля,
        // себестоимость закрывает модель сметы (06), права контракта — части «Плановых работ» (23), чтение и правка КП —
        // адреса и отправка КП (13), правка прайс-листа — его адреса (14). Остальные приходят своими change (календарь и
        // графики — 18). Список назван здесь
        // нарочно: право, добавленное без своего change, обязано столкнуться с этой строкой и с объяснением
        // рядом с ней.
        await ModuleOnlyChecks.DeclaredAndStoredAsync(host, PlanModule.ModuleCode,
            [PlanModule.EstimateCost, PlanModule.EstimateEdit, PlanModule.EstimateHint, PlanModule.EstimateMaterials,
             PlanModule.EstimateRead, PlanModule.LaborRead, PlanModule.OfferEdit, PlanModule.OfferRead, PlanModule.OfferSend, PlanModule.PriceListEdit], "id", "costs", "work");

    /// <summary>
    /// Права модуля либо стоят на двери, либо названы здесь как ещё не носящие ни одной (зеркало
    /// <see cref="EndpointGateInventoryTests" /> для модуля, выключенного на умолчательном хосте — тот
    /// прав <c>plan</c> не видит вовсе, AUTH-19).
    ///
    /// <para>Список — храповик: задача, приносящая дверь, обязана убрать свою строку, иначе тест упадёт
    /// и потребует этого.</para>
    /// </summary>
    [Fact]
    public void Module_permissions_open_a_door_or_are_named_as_doorless() =>
        // Двери смет пришли с моделью сметы (plan-06). Себестоимость (plan.estimate.cost) двери не
        // открывает — она открывает часть ответа, — поэтому на воротах её нет и здесь она названа.
        ModuleOnlyChecks.DoorOrNamedDoorless(host, PlanModule.ModuleCode, new Dictionary<string, string>
        {
            [PlanModule.EstimateCost] =
                "не дверь, а часть ответа: без права ответы смет и строк идут без денег (plan-06, PLAN-11)",
            // Права контракта двери не открывают по построению (AUTH-8.1): их «дверь» — часть ответа контракта
            // «Плановые работы» в модуле-потребителе (plan-23).
            [PlanModule.EstimateMaterials] = "право контракта: часть materials «Плановых работ» (plan-23)",
            [PlanModule.LaborRead] = "право контракта: часть labor «Плановых работ» (plan-23)",
            [PlanModule.EstimateHint] = "право контракта: часть hint «Плановых работ» (plan-23)",
        });

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
    ///   <item>«Менеджер проекта» — чтение смет, БЕЗ себестоимости (решение В-1, PLAN-Q9), и право контракта
    ///   <c>plan.labor.read</c> (<c>plan-23</c>, WORK-57), чтение, правка и отправка КП (<c>plan-13</c>),
    ///   правка прайс-листа (<c>plan-14</c>).</item>
    ///   <item>«Руководитель» — чтение смет с деньгами и КП через «читать всё» (D12 зонтика); правка и права
    ///   контракта (<c>ReadAllMark.Out</c>) в него не входят.</item>
    ///   <item>«Инженер ИД», «Бухгалтер» — модуля и прав нет.</item>
    ///   <item>«Монтажник» и «Снабженец» — модуля нет, а права контракта есть: <c>plan.estimate.hint</c> и
    ///   <c>plan.estimate.materials</c> (<c>plan-23</c>). Модуль они не открывают (AUTH-8.1, <c>plan-21</c>).</item>
    /// </list>
    ///
    /// <para>Что ворота <c>/api/plan</c> закрыты ролям без прав модуля, проверяет первый адрес под ними —
    /// сметы (<c>EstimateEndpointsTests</c>, <c>plan-06</c>); признак модуля и ворота считаются одним
    /// правилом (<c>ModuleAccess</c>), это сверяет <c>AccountAccessTests.Module_flag_matches_the_module_gate</c>.</para>
    /// </summary>
    [Theory]
    [InlineData(SystemRoles.Admin, true, PlanModule.EstimateCost, PlanModule.EstimateEdit, PlanModule.EstimateHint,
        PlanModule.EstimateMaterials, PlanModule.EstimateRead, PlanModule.LaborRead, PlanModule.OfferEdit, PlanModule.OfferRead, PlanModule.OfferSend, PlanModule.PriceListEdit)]
    [InlineData("Estimator", true, PlanModule.EstimateCost, PlanModule.EstimateEdit, PlanModule.EstimateRead)]
    [InlineData("ProjectManager", true, PlanModule.EstimateRead, PlanModule.LaborRead, PlanModule.OfferEdit, PlanModule.OfferRead, PlanModule.OfferSend, PlanModule.PriceListEdit)]
    [InlineData("Executive", true, PlanModule.EstimateCost, PlanModule.EstimateRead, PlanModule.OfferRead)]
    [InlineData(SystemRoles.IdEngineer, false)]
    [InlineData("Accountant", false)]
    [InlineData("Installer", false, PlanModule.EstimateHint)]
    [InlineData("Supplier", false, PlanModule.EstimateMaterials)]
    [InlineData("WorksManager", false)]
    public async Task System_roles_get_exactly_their_plan_permissions(string role, bool opens, params string[] expected) =>
        // Точное равенство — оно же и «у сметчика ни одного plan.offer.*»: лишний код здесь не прошёл бы.
        await ModuleOnlyChecks.RoleGetsExactlyAsync(host, new PlanModule(), role, opens, expected);

    /// <summary>
    /// Права КОНТРАКТА модуль не открывают (AUTH-8.1, D11 зонтика <c>plan-00-epic</c>): роль, у которой
    /// из прав <c>plan</c> в составе только они, не получает ни признака модуля, ни его ворот.
    ///
    /// <para>Роли отбираются по СОСТАВУ в <c>SystemRoles</c>, а не по ответу доступа, — нарочно (ревью
    /// PR #1580). Права контракта объявлены (<c>plan-23</c>), и право в ответе доступа у роли есть — а модуля
    /// нет: признак права контракта (<c>plan-21</c>) не даёт <c>ModuleAccess.IsOpen</c> открыть модуль по
    /// началу кода. Закрыты и ворота: первый адрес модуля отвечает 403.</para>
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
            var client = await SignInAsync(role);
            var access = await AccessAsync(client);
            var module = Assert.Single(access.Modules, m => m.Code == PlanModule.ModuleCode);
            Assert.False(module.Available,
                $"Роль «{role}» получила модуль plan, имея из его прав только права контракта. Признак права " +
                "контракта (plan-21) обязан появиться раньше, чем права контракта объявят (plan-23).");
            // Право объявлено и до роли дошло — иначе проверка выше была бы зелёной оттого, что проверять нечего.
            Assert.Contains(access.Permissions, c => contract.Contains(c));
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/plan/estimates")).StatusCode);
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
/// <para>⚠️ Схема <c>plan</c> СНОСИТСЯ до запуска (ревью PR #1580): без этого проверка «схема
/// мигрирована при старте» проходила бы на схеме, оставшейся от прошлого прогона, — и с вырезанной
/// миграцией модуля тоже. Так же устроен <see cref="ModuleSchemaHost" /> для <c>costs</c>. Как и когда
/// сносится — <see cref="ModuleOnlyHost" />.</para>
/// </summary>
public sealed class PlanOnlyHost : ModuleOnlyHost
{
    protected override string ModuleCode => PlanModule.ModuleCode;

    protected override string DatabaseSuffix => "plan";

    protected override IReadOnlyList<string> SchemasDroppedBeforeStart => [PlanDbContext.SchemaName];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => services.AddSingleton<IDocumentGeneratorFactory, PlanOnlyPrintStub>());
    }
}

internal sealed class PlanOnlyPrintStub : IDocumentGeneratorFactory, IDocumentGenerator
{
    internal GenerationRequest? LastRequest { get; private set; }

    public IDocumentGenerator Create(OutputFormat format) => this;

    public Task<byte[]> GenerateAsync(GenerationRequest request, CancellationToken ct = default)
    {
        LastRequest = request;
        return Task.FromResult("%PDF"u8.ToArray());
    }

    public Task<byte[]> DebugBundleAsync(GenerationRequest request, BundleNotes notes, CancellationToken ct = default) =>
        throw new NotSupportedException("Эта подмена проверяет запрос к генератору PDF.");
}
