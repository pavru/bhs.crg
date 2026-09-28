using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Установка БЕЗ модуля исполнительной документации: <c>Modules__Enabled=costs</c> (задача A1
/// этапа 2, issue #1068, ТЗ COST-Q1, AUTH-17, AUTH-18, CORE-1).
///
/// <para>Зачем настоящий хост, когда механизм модулей уже проверен. <see cref="Configuration.DisabledModuleTests" />
/// проверяет его на ПОДДЕЛЬНЫХ модулях в slim-хосте — то есть проверяет механизм, а не приложение.
/// Настоящее приложение без <c>id</c> до этой задачи не поднимал никто, и поднять его — единственный
/// способ узнать, не опирается ли ядро на модуль, которого может не быть. Ровно это и есть признак
/// готовности A1, и ровно этим проверяется первый пункт этапа по ТЗ («установка только счета
/// распознаёт без модуля ИД», `CORE-Q6`): без каркаса модуля показать его нечем — выключить
/// единственный модуль нельзя, пустая настройка означает умолчание.</para>
///
/// <para>⚠️ Хост живёт на СВОЕЙ базе, и это не украшение. Состав системной роли приводится при старте
/// к объявленному (<see cref="RoleSynchronizer" />), а с выключенным <c>id</c> его прав в каталоге
/// нет — то есть на общей тестовой базе этот хост снял бы у «Инженера ИД» и «Снабженца» все
/// <c>id.*</c>, и соседние классы падали бы на правах, которых им никто не отбирал. Искали бы
/// причину у них.</para>
/// </summary>
public class CostsOnlyHostTests(CostsOnlyHost host) : IClassFixture<CostsOnlyHost>
{
    /// <summary>
    /// Приложение поднимается без <c>id</c>, а его адреса отвечают отказом, который называет причину
    /// и модуль (501, ТЗ OVW-10, AUTH-15, AUTH-19).
    ///
    /// Самое ценное здесь — первая строка: до этой задачи <c>Modules__Enabled=costs</c> останавливал
    /// старт словами «названы модули, которых в этой сборке нет».
    /// </summary>
    [Fact]
    public async Task Host_starts_without_the_id_module_and_its_addresses_refuse()
    {
        var client = host.CreateClient();

        var registry = host.Services.GetRequiredService<ModuleRegistry>();
        Assert.Equal(["costs"], registry.Enabled.Select(m => m.Code));
        Assert.Contains("id", registry.Disabled.Select(m => m.Code));

        var response = await client.GetAsync("/api/quality-docs");

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("id", body.GetProperty("module").GetString());
        Assert.Contains("Исполнительная документация", body.GetProperty("error").GetString());
    }

    /// <summary>
    /// Включённый модуль отказом НЕ отвечает: под его путём обычный 404, потому что адресов у каркаса
    /// пока нет.
    ///
    /// Различие не косметическое. 501 означает «экземпляр этого не умеет и не научится сам»; получив
    /// его от включённого модуля, человек пошёл бы включать то, что уже включено. Сторож стоит здесь,
    /// пока адресов нет: с первым адресом (C1) проверять это станет нечем — 404 перестанет быть
    /// достижимым, а подмена кода останется возможной.
    /// </summary>
    [Fact]
    public async Task Enabled_module_does_not_refuse_its_own_paths()
    {
        var response = await host.CreateClient().GetAsync("/api/costs/invoices");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Права модуля объявлены с объяснениями, доезжают до базы — и прав выключенного модуля в
    /// каталоге нет вовсе (AUTH-1, AUTH-19).
    /// </summary>
    [Fact]
    public async Task Costs_permissions_are_declared_with_explanations_and_reach_the_database()
    {
        _ = host.CreateClient();

        var catalog = host.Services.GetRequiredService<PermissionCatalog>();
        var costs = catalog.All.Where(p => p.Code.StartsWith("costs.", StringComparison.Ordinal)).ToList();

        // Восемь, а не одиннадцать по перечню ТЗ: согласование (C6), заявки на закупку (этап 3) и
        // costs.materials.read (контракт «Материалы на объекте», G6) вынесены — см. причины в
        // CostsModule. Число названо здесь нарочно: право, добавленное без причины, обязано
        // столкнуться с этой строкой и с объяснением рядом с ней.
        Assert.Equal(8, costs.Count);
        Assert.All(costs, p => Assert.Null(p.Validate()));
        Assert.DoesNotContain(catalog.Codes, c => c.StartsWith("id.", StringComparison.Ordinal));

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Проверка изоляции стоит рядом с чтением базы, а не в стороне: тест, случайно пришедший на
        // общую тестовую базу, снял бы у ролей права `id.*` и уронил бы соседние классы.
        Assert.Contains("_costs", db.Database.GetConnectionString() ?? string.Empty);

        var stored = await db.Permissions.Where(p => p.Code.StartsWith("costs.")).ToListAsync();
        foreach (var declared in costs)
        {
            var row = stored.SingleOrDefault(p => p.Code == declared.Code);
            Assert.True(row is not null, $"Право «{declared.Code}» объявлено модулем, но в базу не попало.");
            Assert.True(row!.IsDeclared, $"Право «{declared.Code}» объявлено, но помечено как невыдаваемое.");
            Assert.Equal(declared.Gives, row.Gives);
        }
    }

    /// <summary>
    /// Системные роли модуля — «Снабженец» и «Бухгалтер» — получают его права без правки состава
    /// (ТЗ AUTH-4, COST-28).
    ///
    /// Состав обеих объявлен ядром давно и целиком, включая права, которых в сборке не было: они
    /// молча пропускались. Тест проверяет, что пропуск закончился ровно тогда, когда модуль появился,
    /// — иначе роль «Снабженец» существовала бы пустой, и первым это заметил бы снабженец.
    ///
    /// И обратная половина: оплата у снабженца НЕ появляется (COST-28, COST-29). Проверка «права
    /// выданы» без неё зелена и у роли, которой выдали всё.
    /// </summary>
    [Theory]
    [InlineData("Supplier", "costs.invoice.edit", "costs.allocation.edit", "costs.waybill.edit")]
    [InlineData("Accountant", "costs.invoice.pay", "costs.report.read", "costs.articles.edit")]
    public async Task Module_roles_receive_module_permissions(string role, params string[] expected)
    {
        _ = host.CreateClient();

        using var scope = host.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();

        var found = await roles.FindByNameAsync(role);
        Assert.True(found is not null, $"Системной роли «{role}» нет: её заводит ядро при старте.");

        var granted = (await roles.GetClaimsAsync(found!))
            .Where(c => c.Type == RoleSynchronizer.PermissionClaim)
            .Select(c => c.Value)
            .ToList();

        foreach (var code in expected)
            Assert.Contains(code, granted);

        if (role == "Supplier")
            Assert.DoesNotContain("costs.invoice.pay", granted);
    }
}

/// <summary>
/// Хост «только счета»: <c>Modules__Enabled=costs</c> и отдельная база.
///
/// Наследуется от <see cref="IntegrationTestFixture" />, чтобы не разойтись с ним в том, чем
/// тестовый хост вообще держится (подставное хранилище, снятые расписания, ослабленные пределы
/// частоты). Переопределения добавляются ПОСЛЕ базовых — слой конфигурации, добавленный последним,
/// выигрывает, и одного <c>UseSetting</c> тут не хватило бы: базовый класс кладёт те же ключи ещё и
/// слоем на <c>Build</c>, то есть поверх настроек хоста.
/// </summary>
public sealed class CostsOnlyHost : IntegrationTestFixture
{
    /// <summary>
    /// Своя база — «…_costs» рядом с общей тестовой. Создаётся сама: приложение мигрирует схему при
    /// старте, а миграция создаёт базу.
    /// </summary>
    private static string ConnectionString { get; } = Dedicated();

    private static string Dedicated()
    {
        var builder = new NpgsqlConnectionStringBuilder(TestConnectionString);
        builder.Database += "_costs";
        return builder.ConnectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        var overrides = new Dictionary<string, string?>
        {
            ["Modules:Enabled"] = "costs",
            ["ConnectionStrings:Postgres"] = ConnectionString,
        };

        foreach (var (key, value) in overrides) builder.UseSetting(key, value);
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(overrides));
    }
}
