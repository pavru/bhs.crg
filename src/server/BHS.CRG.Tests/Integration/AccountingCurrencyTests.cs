using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Валюта учёта — настройка экземпляра (ТЗ COST-7.2, issue #1248): пути записи целиком, от адреса
/// до базы. Здесь — экземпляр БЕЗ документов: валюту менять можно.
/// </summary>
[Collection("Integration")]
public class AccountingCurrencyTests(IntegrationTestFixture fixture)
{
    private const string Password = "Test-1234!";
    private const string Url = "/api/settings/company/currency";

    [Fact]
    public async Task Пока_документов_нет_валюта_меняется_доходит_до_каждого_и_пишется_в_журнал()
    {
        await fixture.ResetDatabaseAsync();
        var client = await SignInAsync();

        var before = await ReadAsync(client, Url);
        Assert.Equal("RUB", before.GetProperty("currency").GetString());
        Assert.False(before.GetProperty("locked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("reason").ValueKind);
        Assert.Equal(["RUB", "BYN", "KZT", "USD", "EUR", "CNY"],
            before.GetProperty("currencies").EnumerateArray().Select(c => c.GetProperty("code").GetString()));

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync(Url, new { currency = "KZT" })).StatusCode);

        // Форматтеру клиента валюту отдаёт адрес, который читает каждый вошедший, — не адрес настройки.
        var access = await ReadAsync(client, "/api/account/access");
        Assert.Equal("KZT", access.GetProperty("accounting").GetProperty("currency").GetString());

        var written = Assert.Single(await ChangesAsync());
        Assert.Equal(("Российский рубль (RUB)", "Казахстанский тенге (KZT)"), (written.Before, written.After));

        // Та же валюта повторно — не смена: второй записи в журнале нет.
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync(Url, new { currency = "KZT" })).StatusCode);
        Assert.Single(await ChangesAsync());
    }

    [Theory]
    [InlineData("kzt")]
    [InlineData("ТЕНГЕ")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Валюта_не_из_списка_отказ(string? currency)
    {
        await fixture.ResetDatabaseAsync();
        var client = await SignInAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(Url, new { currency })).StatusCode);
        Assert.Empty(await ChangesAsync());
    }

    /// <summary>
    /// Строка не из списка, оказавшаяся в базе, экран не роняет: действует рубль, а администратор
    /// видит, что лежит на самом деле. Каждому вошедшему уходит только код из списка.
    /// </summary>
    [Fact]
    public async Task Негодная_строка_в_базе_читается_рублём_и_названа_администратору()
    {
        await fixture.ResetDatabaseAsync();
        var client = await SignInAsync();
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IAppSettingsStore>()
                .SetAsync(AppSettingKeys.AccountingCurrency, "рубли");

        var view = await ReadAsync(client, Url);
        Assert.Equal("RUB", view.GetProperty("currency").GetString());
        Assert.Equal("рубли", view.GetProperty("stale").GetString());

        var access = await ReadAsync(client, "/api/account/access");
        Assert.Equal("RUB", access.GetProperty("accounting").GetProperty("currency").GetString());
    }

    /// <summary>
    /// Объявление модуля счетов сверено с его моделью: имя таблицы уходит в запрос текстом, и
    /// опечатка в нём читалась бы как «таблицы нет — документов нет», то есть сняла бы запрет молча.
    /// </summary>
    [Fact]
    public void Учётные_таблицы_модуля_счетов_есть_в_его_модели()
    {
        using var db = new CostsDbContextFactory().CreateDbContext([]);
        var tables = db.Model.GetEntityTypes().Select(e => e.GetTableName()).ToHashSet();

        var declared = new CostsModule().AccountingDocuments;

        Assert.Equal(["invoices", "waybills"], declared.Select(d => d.Table));
        Assert.All(declared, d => Assert.Null(d.Validate()));
        Assert.All(declared, d => Assert.Contains(d.Table, tables));
    }

    private async Task<IReadOnlyList<BHS.CRG.Domain.Activity.ActivityRecord>> ChangesAsync()
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IActivityLog>()
            .ReadAsync(0, 50, ActivityVisibility.Whole, ActivityActions.AccountingCurrencyChanged.Code);
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<HttpClient> SignInAsync()
    {
        var email = $"currency_{Guid.NewGuid():N}@test.local";
        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "Тест", EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, SystemRoles.Admin)).Succeeded);
        }

        var client = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

/// <summary>
/// Валюта учёта на экземпляре СО счетами (issue #1248): смена заперта, причина названа. Хост счетов
/// общий и базу между тестами не чистит — документы в нём есть всегда, этим тест и пользуется.
/// </summary>
public class AccountingCurrencyLockedTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    private const string Url = "/api/settings/company/currency";

    /// <summary>
    /// Валюта возвращается к умолчанию после теста. Хост общий и базу между тестами не чистит: если
    /// запрет однажды не сработает, записанный доллар достался бы всем соседям — и этому же тесту при
    /// следующем прогоне, уже под видом «значение не пусто».
    /// </summary>
    public override async Task DisposeAsync()
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAppSettingsStore>()
            .SetAsync(AppSettingKeys.AccountingCurrency, null);
    }

    /// <summary>
    /// Сторож задачи: при существующем счёте смена отказывает, значение и журнал не тронуты.
    /// Сломается, если сверка запрета уйдёт из транзакции записи или перестанет видеть счета.
    /// </summary>
    [Fact]
    public async Task При_существующих_счетах_смена_валюты_отказывает_и_называет_причину()
    {
        var (client, _) = await SignInAsync(SystemRoles.Admin);
        await CreateAsync(client);

        var refused = await client.PutAsJsonAsync(Url, new { currency = "USD" });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var error = (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        Assert.Contains("в модуле «Счета и накладные» — счетов:", error);
        Assert.Contains("не пересчитываются", error);

        var view = await (await client.GetAsync(Url)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(view.GetProperty("locked").GetBoolean());
        Assert.Equal(error, view.GetProperty("reason").GetString());
        Assert.Equal("RUB", view.GetProperty("currency").GetString());

        using var scope = host.Services.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IAppSettingsStore>()
            .GetAsync(AppSettingKeys.AccountingCurrency));
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<IActivityLog>()
            .ReadAsync(0, 5, ActivityVisibility.Whole, ActivityActions.AccountingCurrencyChanged.Code));

        // Тот же рубль записать можно и при счетах: действующее значение не меняется.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(Url, new { currency = "RUB" })).StatusCode);
    }
}
