using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Tables;
using BHS.CRG.Domain.Catalog;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Таблица счетов — оба потребителя одного объявления (задача G1b, issue #1089, ТЗ CORE-33, CORE-24).
///
/// <para>Главная проверка — <see cref="Колонка_сумм_без_права_приходит_с_причиной_а_не_исчезает" />:
/// роль без права на счета видит столько же колонок, сколько снабженец, но суммы — с причиной и без
/// значений. Вернуть меньше колонок — тест падает на сравнении состава.</para>
/// </summary>
public sealed class ModuleTableTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    private const string Address = "costs.invoices";
    private const string Marker = "system:table:" + Address;

    [Fact]
    public async Task Колонка_сумм_без_права_приходит_с_причиной_а_не_исчезает()
    {
        var (supplier, _) = await SignInAsync("Supplier");
        var invoice = await CreateAsync(supplier);
        await OkAsync(await supplier.PutAsJsonAsync($"/api/costs/invoices/{invoice}", new
        {
            requisites = await RequisitesWithAsync(supplier, invoice, "Итого", 1_234.5m),
        }));
        var number = (await ReadAsync(supplier, invoice)).GetProperty("requisites").GetProperty("Номер").GetString();

        var full = await TableAsync(supplier);
        var (waybills, _) = await SignInAsync(await RoleAsync("costs.waybill.read"));
        var narrow = await TableAsync(waybills);

        // Столько же колонок и в том же порядке — меньше не приходит никогда.
        Assert.Equal(Keys(full), Keys(narrow));

        var total = Column(narrow, "Итого");
        Assert.Equal(TableColumnReasons.NoRight, total.GetProperty("unavailable").GetString());
        Assert.Equal("нет права на суммы", total.GetProperty("reason").GetString());
        Assert.Equal(TableColumnReasons.NoRight, Column(narrow, "ВТомЧислеНДС").GetProperty("unavailable").GetString());
        Assert.Equal(JsonValueKind.Null, Column(full, "Итого").GetProperty("unavailable").ValueKind);
        Assert.Equal(JsonValueKind.Null, Column(narrow, "Номер").GetProperty("unavailable").ValueKind);

        // Значения: у снабженца сумма есть, у роли без права ключа нет вовсе — а номер есть у обоих.
        Assert.Equal(1_234.5m, Row(full, number!).GetProperty("Итого").GetDecimal());
        var row = Row(narrow, number!);
        Assert.False(row.TryGetProperty("Итого", out _));
        Assert.False(row.TryGetProperty("ВТомЧислеНДС", out _));
        Assert.Equal("ООО «Кабель-Торг»", row.GetProperty("Поставщик").GetString());
    }

    /// <summary>Колонки типизированы, и операторы у каждой — по её виду.</summary>
    [Fact]
    public async Task Колонки_типизированы_и_несут_свои_операторы()
    {
        var (supplier, _) = await SignInAsync("Supplier");
        var table = await TableAsync(supplier);

        var total = Column(table, "Итого");
        Assert.Equal("number", total.GetProperty("kind").GetString());
        Assert.Contains("gt", total.GetProperty("operators").EnumerateArray().Select(o => o.GetString()));
        Assert.DoesNotContain("contains", total.GetProperty("operators").EnumerateArray().Select(o => o.GetString()));
        Assert.Equal("date", Column(table, "Дата").GetProperty("kind").GetString());
        Assert.Equal("счёт", table.GetProperty("grain").GetString());
    }

    /// <summary>Поле сохранённого представления, которого больше нет, — колонка с причиной, а не пропажа.</summary>
    [Fact]
    public async Task Запрошенное_поле_которого_нет_в_типе_приходит_с_причиной()
    {
        var (supplier, _) = await SignInAsync("Supplier");
        var table = await (await supplier.GetAsync($"/api/tables/{Address}?columns=Номер,Удалённое")).Content
            .ReadFromJsonAsync<JsonElement>();

        Assert.Equal(["Номер", "Удалённое"], Keys(table));
        var removed = Column(table, "Удалённое");
        Assert.Equal(TableColumnReasons.Removed, removed.GetProperty("unavailable").GetString());
        Assert.Equal(TableColumnReasons.RemovedText, removed.GetProperty("reason").GetString());
    }

    /// <summary>
    /// Второй потребитель — набор данных: та же колонка, тот же отказ в значении, и причина — оговоркой
    /// к данным. Колонка из набора не пропадает: разметка на неё выглядела бы как «поле удалено».
    /// </summary>
    [Fact]
    public async Task Набор_данных_на_таблице_отдаёт_колонку_без_права_с_оговоркой()
    {
        var (_, user) = await SignInAsync(await RoleAsync("costs.waybill.read"));

        using var scope = host.Services.CreateScope();
        var access = await scope.ServiceProvider.GetRequiredService<DataAccessResolver>().ForUserAsync(user, default);
        var provider = scope.ServiceProvider.GetServices<ISystemDataProvider>().Single(p => p.Handles(Marker));

        Assert.Equal("costs", provider.Declaration.Module);
        var provided = await provider.ProvideAsync(Marker, CatalogScope.System, null, access, default);

        Assert.Contains(provided.Columns, c => c.Name == "Итого");
        Assert.All(provided.Rows, r => Assert.False(r.ContainsKey("Итого")));
        Assert.Contains("нет права на суммы", provided.Warning);
        Assert.Contains("«Сумма к оплате»", provided.Warning);
    }

    /// <summary>Без доступа к модулю таблица не открывается вовсе — отказ, а не пустые строки.</summary>
    [Fact]
    public async Task Без_модуля_таблица_отказывает()
    {
        var (installer, _) = await SignInAsync("Installer");

        Assert.Equal(HttpStatusCode.Forbidden, (await installer.GetAsync($"/api/tables/{Address}")).StatusCode);
        var list = await installer.GetFromJsonAsync<JsonElement>("/api/tables");
        Assert.DoesNotContain(list.EnumerateArray(), t => t.GetProperty("address").GetString() == Address);
    }

    private static async Task<JsonElement> TableAsync(HttpClient client)
    {
        var response = await client.GetAsync($"/api/tables/{Address}");
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string[] Keys(JsonElement table) =>
        [.. table.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("key").GetString()!)];

    private static JsonElement Column(JsonElement table, string key) =>
        table.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("key").GetString() == key);

    private static JsonElement Row(JsonElement table, string number) =>
        table.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("Номер").GetString() == number);

    /// <summary>Своя роль с одним правом — системных ролей «модуль есть, счетов нет» не бывает.</summary>
    private async Task<string> RoleAsync(string permission)
    {
        var name = $"Narrow_{Guid.NewGuid():N}";
        using var scope = host.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var role = new IdentityRole<Guid>(name);
        Assert.True((await roles.CreateAsync(role)).Succeeded);
        Assert.True((await roles.AddClaimAsync(role, new Claim(RoleSynchronizer.PermissionClaim, permission))).Succeeded);
        return name;
    }
}
