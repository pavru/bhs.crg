using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Tables;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Что таблица счетов отдаёт ЭКРАНУ сверх строк (задача G1e, issue #1092, ТЗ CORE-33): описание без
/// строк, код права у закрытой колонки и строку по ключу.
///
/// <para>Правила ядра про ключ — на поддельной службе, в <c>ModuleTableRowKeyTests</c>. Здесь —
/// настоящая служба счетов: что ключом она называет счёт и что строку по ключу отбирает ВМЕСТЕ с
/// отбором, а не вместо него.</para>
/// </summary>
// ⚠️ Собрание указывается КАЖДОМУ классу: атрибут не наследуется от базового.
[Collection("Integration")]
public sealed class ModuleTableScreenTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    private const string Address = "costs.invoices";

    /// <summary>
    /// Описание — те же колонки, что у таблицы без запроса колонок, и ни одной строки. Строка над
    /// таблицей «скрыто: нет права на суммы» называет КОД права — его и несёт закрытая колонка.
    /// </summary>
    [Fact]
    public async Task Описание_отдаёт_все_колонки_без_строк_и_код_права_у_закрытой()
    {
        var (supplier, _) = await SignInAsync("Supplier");
        var (waybills, _) = await SignInAsync(await RoleAsync("costs.waybill.read"));

        var full = await GetAsync(supplier, $"/api/tables/{Address}/columns");
        var narrow = await GetAsync(waybills, $"/api/tables/{Address}/columns");

        // Таблица без списка колонок — всё объявленное, кроме приходящего только по требованию (issue
        // #1186): описание такие колонки называет, иначе экрану нечем было бы их предложить.
        Assert.Equal(
            Keys(await GetAsync(supplier, $"/api/tables/{Address}")),
            full.GetProperty("columns").EnumerateArray().Where(c => !c.GetProperty("onDemand").GetBoolean())
                .Select(c => c.GetProperty("key").GetString()!).ToArray());
        Assert.Contains(BHS.CRG.Modules.Costs.Tables.InvoiceTable.LostKey, Keys(full));
        Assert.Equal(Keys(full), Keys(narrow));
        Assert.False(full.TryGetProperty("rows", out _));
        Assert.Equal("Счета на оплату", full.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, full.GetProperty("state").ValueKind);

        var closed = Column(narrow, "Итого");
        Assert.Equal(TableColumnReasons.NoRight, closed.GetProperty("unavailable").GetString());
        Assert.Equal("costs.invoice.read", closed.GetProperty("requires").GetString());

        // Открытой колонке кода права не положено: он значит «не хватило», а не «бывает нужно».
        Assert.Equal(JsonValueKind.Null, Column(full, "Итого").GetProperty("requires").ValueKind);
        Assert.Equal(JsonValueKind.Null, Column(narrow, "Номер").GetProperty("requires").ValueKind);

        // И в таблице со строками — тот же код: экран берёт его оттуда, откуда взял колонку.
        Assert.Equal("costs.invoice.read",
            Column(await GetAsync(waybills, $"/api/tables/{Address}"), "Итого").GetProperty("requires").GetString());
    }

    /// <summary>Описание закрыто теми же воротами, что и строки: иначе состав таблицы читался бы в обход.</summary>
    [Fact]
    public async Task Описание_закрыто_теми_же_воротами_что_и_строки()
    {
        var (installer, _) = await SignInAsync("Installer");
        var (supplier, _) = await SignInAsync("Supplier");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await installer.GetAsync($"/api/tables/{Address}/columns")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await supplier.GetAsync("/api/tables/costs.net-takoy/columns")).StatusCode);
    }

    [Fact]
    public async Task Ключ_строки_называет_счёт_и_строка_читается_по_нему_со_всеми_колонками()
    {
        var (supplier, _) = await SignInAsync("Supplier");
        var first = await CreateAsync(supplier);
        var second = await CreateAsync(supplier);

        // Таблица — с двумя колонками: строка по ключу обязана прийти со всеми, а не с показанными.
        var table = await GetAsync(supplier, $"/api/tables/{Address}?columns=Номер,Дата&limit=1000");
        var keys = table.GetProperty("keys").EnumerateArray().Select(k => k.GetString()).ToList();
        Assert.Equal(table.GetProperty("rows").GetArrayLength(), keys.Count);
        Assert.Contains(first.ToString(), keys);
        Assert.Contains(second.ToString(), keys);

        var row = await GetAsync(supplier, $"/api/tables/{Address}?row={second}");
        Assert.Equal([second.ToString()], row.GetProperty("keys").EnumerateArray().Select(k => k.GetString()));
        Assert.Equal(1, row.GetProperty("count").GetInt32());
        var values = Assert.Single(row.GetProperty("rows").EnumerateArray());
        Assert.Equal(Number(await ReadAsync(supplier, second)), values.GetProperty("Номер").GetString());
        Assert.True(values.TryGetProperty("СостояниеОплаты", out _));

        // И телом запроса — так экран ходит с длинным отбором.
        var posted = await supplier.PostAsJsonAsync($"/api/tables/{Address}/query", new { row = first.ToString() });
        await OkAsync(posted);
        Assert.Equal([first.ToString()], (await posted.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("keys").EnumerateArray().Select(k => k.GetString()));
    }

    /// <summary>
    /// Куда ведёт строка (G4, issue #1097): описание называет ТИП ЗАПИСИ, а ключ строки — её
    /// идентификатор; по этой паре экран открывает форму счёта. Проверяются обе половины разом — и
    /// настоящим адресом счёта: тип без ключа-идентификатора дал бы ссылку, которая ведёт в отказ.
    ///
    /// <para>Тип называется и тому, у кого права на счета нет: это не данные, а устройство таблицы.
    /// Пойдёт ли человек по ссылке, решает право экрана — и адрес счёта ему отказывает.</para>
    /// </summary>
    [Fact]
    public async Task Описание_называет_тип_записи_а_ключ_строки_открывает_её_адресом_счёта()
    {
        var (supplier, _) = await SignInAsync("Supplier");
        var (waybills, _) = await SignInAsync(await RoleAsync("costs.waybill.read"));
        var invoice = await CreateAsync(supplier);

        foreach (var client in new[] { supplier, waybills })
            Assert.Equal("СчётНаОплату",
                (await GetAsync(client, $"/api/tables/{Address}/columns")).GetProperty("recordType").GetString());

        var key = Assert.Single((await GetAsync(waybills, $"/api/tables/{Address}?row={invoice}"))
            .GetProperty("keys").EnumerateArray()).GetString();
        Assert.Equal(invoice, (await GetAsync(supplier, $"/api/costs/invoices/{key}")).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Forbidden, (await waybills.GetAsync($"/api/costs/invoices/{key}")).StatusCode);
    }

    /// <summary>
    /// Расшифровка строки закрыта тем же правом, что суммы таблицы (G4, issue #1097): «Доля» следует за
    /// «Суммой». Тот, кому суммы закрыты, видит объекты, колонку доли — с той же причиной и без
    /// значений, итогов расшифровки нет, а подписи модуля нет вовсе: вычистить из неё сумму нечем.
    /// </summary>
    [Fact]
    public async Task Расшифровка_строки_без_права_на_суммы_приходит_без_денег()
    {
        var (supplier, _) = await SignInAsync("Supplier");
        var (waybills, _) = await SignInAsync(await RoleAsync("costs.waybill.read"));
        var invoice = await CreateAsync(supplier, complete: true);
        await OkAsync(await supplier.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(supplier, invoice, "Итого", 7_000m) }));

        var seen = (await GetAsync(supplier, $"/api/tables/{Address}?row={invoice}")).GetProperty("breakdown");
        Assert.All(seen.GetProperty("columns").EnumerateArray(), c => Assert.Equal(JsonValueKind.Null, c.GetProperty("unavailable").ValueKind));
        Assert.NotEmpty(seen.GetProperty("totals").EnumerateArray());
        Assert.Equal("счёт не оплачен — в затраты не вошёл", seen.GetProperty("note").GetString());

        var closed = (await GetAsync(waybills, $"/api/tables/{Address}?row={invoice}")).GetProperty("breakdown");
        var share = closed.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("key").GetString() == "Доля");
        Assert.Equal("no-right", share.GetProperty("unavailable").GetString());
        Assert.Equal("нет права на суммы", share.GetProperty("reason").GetString());
        // У счёта без разноски строка одна — остаток, и существует она только из-за денег: само её
        // название было бы фактом о суммах, поэтому тому, кому они закрыты, она не приходит.
        Assert.Single(seen.GetProperty("rows").EnumerateArray());
        Assert.Empty(closed.GetProperty("rows").EnumerateArray());
        Assert.Empty(closed.GetProperty("totals").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, closed.GetProperty("note").ValueKind);
    }

    /// <summary>
    /// Строка читается под ТЕМ ЖЕ отбором, что и таблица: счёт вне отбора по ключу не приходит. Иначе
    /// панель показывала бы строку, которой в таблице под этим отбором нет.
    /// </summary>
    [Fact]
    public async Task Строка_вне_отбора_по_ключу_не_приходит()
    {
        var (supplier, _) = await SignInAsync("Supplier");
        var mine = await CreateAsync(supplier);
        var other = await CreateAsync(supplier);
        var filter = Uri.EscapeDataString(JsonSerializer.Serialize(new
        {
            type = "condition", column = "Номер", op = "eq", value = Number(await ReadAsync(supplier, other)),
        }));

        var outside = await GetAsync(supplier, $"/api/tables/{Address}?row={mine}&filter={filter}");
        Assert.Empty(outside.GetProperty("rows").EnumerateArray());
        Assert.Empty(outside.GetProperty("keys").EnumerateArray());
        Assert.Equal(0, outside.GetProperty("count").GetInt32());

        var inside = await GetAsync(supplier, $"/api/tables/{Address}?row={other}&filter={filter}");
        Assert.Single(inside.GetProperty("rows").EnumerateArray());
    }

    /// <summary>Ключ, который не разбирается, — «такой строки нет», а не все строки.</summary>
    [Fact]
    public async Task Ключ_который_не_разбирается_не_отдаёт_ни_одной_строки()
    {
        var (supplier, _) = await SignInAsync("Supplier");
        await CreateAsync(supplier);

        var table = await GetAsync(supplier, $"/api/tables/{Address}?row=ne-kluch");

        Assert.Empty(table.GetProperty("rows").EnumerateArray());
        Assert.Equal(0, table.GetProperty("count").GetInt32());
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string? Number(JsonElement invoice) =>
        invoice.GetProperty("requisites").GetProperty("Номер").GetString();

    private static string[] Keys(JsonElement table) =>
        [.. table.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("key").GetString()!)];

    private static JsonElement Column(JsonElement table, string key) =>
        table.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("key").GetString() == key);

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
