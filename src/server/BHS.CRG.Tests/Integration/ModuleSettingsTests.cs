using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Settings;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Настройки модуля (задача M1, issue #1070): модуль объявляет ключ, администратор его видит и
/// правит, значение ДЕЙСТВУЕТ. Последнее — главное: настройка, которая сохраняется и не действует,
/// выглядит исправной на своём экране, и узнать о поломке можно только отсюда.
/// </summary>
[Collection("Integration")]
public class ModuleSettingsTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    private const string Key = "costs.allocation.tolerance";

    // Записи журнала этого теста: хост у классов счетов общий и базу между тестами не чистит.
    private readonly DateTimeOffset _since = DateTimeOffset.UtcNow;

    /// <summary>
    /// Допуск возвращается к умолчанию после КАЖДОГО теста. Хост общий и базу между тестами не
    /// чистит: оставленные десять копеек достались бы соседу, и его счёт с расхождением в сорок
    /// перестал бы проходить в «разобран» — по причине, которой в его тесте нет.
    /// </summary>
    public override async Task DisposeAsync()
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAppSettingsStore>().SetAsync(Key, null);
    }

    /// <summary>
    /// Сторож задачи: сменили допуск настоящим адресом — и переход в «разобран» отвечает по-новому.
    /// Сломается, если любое место расчёта вернётся к зашитому рублю.
    /// </summary>
    [Fact]
    public async Task Допуск_сменили_настройкой_и_переход_в_разобран_считает_по_новому()
    {
        var (client, _) = await SignInAsync(SystemRoles.Admin);
        var invoice = await OffByAsync(client, 0.40m);

        // При умолчании (рубль) сорок копеек — в допуске; при десяти копейках — уже нет.
        await SaveAsync(client, "0.10");
        var refused = await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("при допуске 0,10", (await refused.Content.ReadAsStringAsync()).Replace('.', ','));

        await SaveAsync(client, "0.40");
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null));

        var written = await ChangesAsync();
        Assert.Equal(2, written.Count);
        // Новые записи первыми. «Было» у первой смены — умолчание: сохранённого значения не было.
        Assert.Equal(("0,10 ₽", "0,40 ₽"), (written[0].Before, written[0].After));
        Assert.Equal(("1,00 ₽", "0,10 ₽"), (written[1].Before, written[1].After));
        Assert.Equal(Key, written[0].TargetId);
        Assert.Contains("Допуск расхождения сумм", written[0].TargetLabel);
    }

    /// <summary>
    /// Решение владельца 07.10.2026: допуск действует на ВСЕ счета, на счёте он не фиксируется.
    /// Тест записывает следствие, а не одобряет его: разобранный счёт после понижения остаётся
    /// «разобран», но разнесённым не считается и к оплате не допускается. Изменится решение (допуск
    /// станут фиксировать при переходе) — тест обязан упасть и быть переписан осознанно.
    /// </summary>
    [Fact]
    public async Task Понижение_допуска_действует_и_на_уже_разобранный_счёт()
    {
        var (client, _) = await SignInAsync(SystemRoles.Admin);
        var invoice = await OffByAsync(client, 0.40m);
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null));

        var before = await ReadAsync(client, invoice);
        Assert.True(before.GetProperty("allocation").GetProperty("allocated").GetBoolean());
        Assert.Null(before.GetProperty("payment").GetProperty("refusal").GetString());

        await SaveAsync(client, "0.10");

        var after = await ReadAsync(client, invoice);
        Assert.Equal("Разобран", after.GetProperty("requisites").GetProperty("Состояние").GetString());
        var allocation = after.GetProperty("allocation");
        Assert.Equal(0.10m, allocation.GetProperty("tolerance").GetDecimal());
        Assert.False(allocation.GetProperty("withinTolerance").GetBoolean());
        Assert.False(allocation.GetProperty("allocated").GetBoolean());
        Assert.Contains("расходится", after.GetProperty("payment").GetProperty("refusal").GetString());
    }

    [Fact]
    public async Task Администратор_видит_настройку_с_подписью_границами_и_умолчанием()
    {
        var (client, _) = await SignInAsync(SystemRoles.Admin);

        var module = await CostsAsync(client);
        Assert.Equal("Счета и накладные", module.GetProperty("title").GetString());
        var setting = Assert.Single(module.GetProperty("settings").EnumerateArray());

        Assert.Equal(Key, setting.GetProperty("key").GetString());
        Assert.Equal("number", setting.GetProperty("kind").GetString());
        Assert.Equal("1.00", setting.GetProperty("value").GetString());
        Assert.Equal("1.00", setting.GetProperty("default").GetString());
        Assert.Equal(JsonValueKind.Null, setting.GetProperty("stored").ValueKind);
        Assert.Equal(0m, setting.GetProperty("min").GetDecimal());
        Assert.Equal(100m, setting.GetProperty("max").GetDecimal());
        Assert.Equal(2, setting.GetProperty("scale").GetInt32());
        Assert.Equal("₽", setting.GetProperty("unit").GetString());
        Assert.False(string.IsNullOrWhiteSpace(setting.GetProperty("effect").GetString()));
        // Настройка действует на записанные счета — предупреждение обязано ехать с объявлением.
        Assert.Contains("закрытых периодов", setting.GetProperty("changeWarning").GetString());
    }

    /// <summary>«5» и «5.00» — одно значение; снятая настройка возвращает умолчание, а не ноль.</summary>
    [Fact]
    public async Task Значение_хранится_в_одном_виде_а_снятое_возвращает_умолчание()
    {
        var (client, _) = await SignInAsync(SystemRoles.Admin);

        await SaveAsync(client, "5");
        var saved = Assert.Single((await CostsAsync(client)).GetProperty("settings").EnumerateArray());
        Assert.Equal("5.00", saved.GetProperty("stored").GetString());
        Assert.Equal("5.00", saved.GetProperty("value").GetString());

        // То же значение другой записью — не смена: в журнал не идёт.
        await SaveAsync(client, "5.0");
        Assert.Single(await ChangesAsync());

        await SaveAsync(client, null);
        var reset = Assert.Single((await CostsAsync(client)).GetProperty("settings").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, reset.GetProperty("stored").ValueKind);
        Assert.Equal("1.00", reset.GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("рубль", "нужно число")]
    [InlineData("100.01", "допустимо от 0,00 до 100,00 ₽")]
    [InlineData("-0.01", "допустимо от 0,00 до 100,00 ₽")]
    [InlineData("0.005", "не больше 2 знаков")]
    [InlineData("1,5", "нужно число")]
    public async Task Негодное_значение_отвергнуто_с_причиной_у_своего_поля(string value, string reason)
    {
        var (client, _) = await SignInAsync(SystemRoles.Admin);

        var response = await PutAsync(client, "costs", Key, value);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(reason, body.GetProperty("fields").GetProperty(Key).GetString());
        // Отвергнутое не записано: действует прежнее.
        Assert.Equal("1.00", Assert.Single((await CostsAsync(client)).GetProperty("settings").EnumerateArray())
            .GetProperty("value").GetString());
    }

    [Fact]
    public async Task Необъявленный_ключ_чужой_модуль_и_чужое_право_отказ()
    {
        var (admin, _) = await SignInAsync(SystemRoles.Admin);

        var unknown = await PutAsync(admin, "costs", "costs.allocation.tolerence", "1");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("не объявляет", await unknown.Content.ReadAsStringAsync());

        // Ключ ядра адресом модуля не пишется, даже если назвать его: модуль его не объявлял.
        var core = await PutAsync(admin, "costs", AppSettingKeys.CompanyTimeZone, "Europe/Moscow");
        Assert.Equal(HttpStatusCode.BadRequest, core.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(admin, "nope", Key, "1")).StatusCode);

        var (user, _) = await SignInAsync(SystemRoles.IdEngineer);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/settings/modules")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(user, "costs", Key, "5")).StatusCode);
    }

    /// <summary>
    /// Каталог ключей, которым восстановление копии решает, принять ли настройку: ключ модуля
    /// принимается наравне с ключом ядра, а незнакомый и негодный — нет.
    /// </summary>
    [Fact]
    public void Каталог_ключей_знает_настройки_ядра_и_модулей()
    {
        var catalog = host.Services.GetRequiredService<IAppSettingCatalog>();

        Assert.True(catalog.Accepts(AppSettingKeys.CompanyTimeZone, "Europe/Moscow"));
        Assert.True(catalog.Accepts(Key, "0.50"));
        Assert.False(catalog.Accepts(Key, "500"));
        Assert.False(catalog.Accepts("costs.allocation.tolerence", "0.50"));
        Assert.False(catalog.Accepts("nope.some.key", "1"));
    }

    /// <summary>
    /// Настройку, заведённую полем и не вписанную в <c>Settings</c> модуля, порт не читает: у неё
    /// нет ни экрана, ни проверки при старте, и отдавала бы она умолчание вечно.
    /// </summary>
    [Fact]
    public async Task Порт_отказывает_настройке_которую_модуль_не_объявил()
    {
        using var scope = host.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IModuleSettings>();

        Assert.Equal(1.00m, await settings.GetAsync(CostsSettings.AllocationTolerance));

        var stray = new NumberSetting("costs.allocation.stray", "Забытая", "Ни на что не влияет", 1m, 0m, 10m);
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => settings.GetAsync(stray));
        Assert.Contains("не объявлена", refusal.Message);

        // И двойник объявленной — тот же ключ, другой объект: читать можно только то, что объявлено.
        var twin = CostsSettings.AllocationTolerance with { DefaultValue = 50m };
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.GetAsync(twin));
    }

    /// <summary>Счёт, у которого сумма к оплате больше суммы строк на <paramref name="gap" />.</summary>
    private async Task<Guid> OffByAsync(HttpClient client, decimal gap)
    {
        var invoice = await CreateAsync(client, complete: true);
        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}", new
        {
            requisites = await RequisitesWithAsync(client, invoice, "Итого", 100m + gap),
        }));
        await AllocatedLinesAsync(client, invoice, [Line(cable, quantity: 1, price: 100m)]);
        return invoice;
    }

    private static async Task SaveAsync(HttpClient client, string? value) =>
        await OkAsync(await PutAsync(client, "costs", Key, value));

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string module, string key, string? value) =>
        client.PutAsJsonAsync($"/api/settings/modules/{module}",
            new { values = new Dictionary<string, string?> { [key] = value } });

    private static async Task<JsonElement> CostsAsync(HttpClient client)
    {
        var body = await client.GetFromJsonAsync<JsonElement>("/api/settings/modules");
        return body.GetProperty("modules").EnumerateArray().Single(m => m.GetProperty("code").GetString() == "costs");
    }

    private async Task<IReadOnlyList<BHS.CRG.Domain.Activity.ActivityRecord>> ChangesAsync()
    {
        using var scope = host.Services.CreateScope();
        var all = await scope.ServiceProvider.GetRequiredService<IActivityLog>()
            .ReadAsync(0, 50, ActivityVisibility.Whole, ActivityActions.ModuleSettingChanged.Code);
        return [.. all.Where(r => r.OccurredAt >= _since)];
    }
}
