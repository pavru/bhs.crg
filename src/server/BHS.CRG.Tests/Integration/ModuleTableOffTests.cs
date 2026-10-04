using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Tables;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Таблица выключенного модуля (задача G1b, issue #1089, ТЗ CORE-33, AUTH-19). Общий хост прогона
/// поднят без <c>costs</c> — то есть ровно в том состоянии, которое проверяется.
///
/// <para>Таблица не пропадает пустым 404: колонки приходят все, каждая с причиной «модуль выключен»,
/// и строк нет. Это третья из трёх причин, и она обязана отличаться от двух других — иначе на экране
/// «модуль выключен» читалось бы как «нет права».</para>
/// </summary>
[Collection("Integration")]
public class ModuleTableOffTests(IntegrationTestFixture fixture)
{
    private const string Password = "Passw0rd!";

    [Fact]
    public async Task Таблица_выключенного_модуля_отдаёт_все_колонки_с_причиной()
    {
        var client = await AdminAsync();

        var table = await client.GetFromJsonAsync<JsonElement>("/api/tables/costs.invoices");

        Assert.Equal(TableColumnReasons.ModuleOff, table.GetProperty("state").GetString());
        Assert.Empty(table.GetProperty("rows").EnumerateArray());
        var columns = table.GetProperty("columns").EnumerateArray().ToList();
        Assert.Equal(18, columns.Count);
        Assert.All(columns, c =>
        {
            Assert.Equal(TableColumnReasons.ModuleOff, c.GetProperty("unavailable").GetString());
            Assert.Equal("модуль «Счета и накладные» выключен", c.GetProperty("reason").GetString());
        });

        // Схему типа выключенный модуль не читает (ревью PR #1130): отвечает тем, что лежит в его коде.
        // Запрошенное поле сохранённого представления всё равно приходит — своим ключом, с той же
        // причиной, а не «удалено из типа».
        var view = await client.GetFromJsonAsync<JsonElement>("/api/tables/costs.invoices?columns=Номер,ПолеЗаказчика");
        var viewColumns = view.GetProperty("columns").EnumerateArray().ToList();
        Assert.Equal(["Номер", "ПолеЗаказчика"], viewColumns.Select(c => c.GetProperty("key").GetString()));
        Assert.All(viewColumns, c => Assert.Equal(TableColumnReasons.ModuleOff, c.GetProperty("unavailable").GetString()));

        // Описание без строк (G1e, issue #1092) говорит то же: состояние названо, колонки — объявленные,
        // каждая с причиной. Экран по нему и рисует «модуль выключен» вместо пустой таблицы.
        var described = await client.GetFromJsonAsync<JsonElement>("/api/tables/costs.invoices/columns");
        Assert.Equal(TableColumnReasons.ModuleOff, described.GetProperty("state").GetString());
        Assert.Equal(
            columns.Select(c => c.GetProperty("key").GetString()),
            described.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("key").GetString()));
        Assert.All(described.GetProperty("columns").EnumerateArray(),
            c => Assert.Equal(TableColumnReasons.ModuleOff, c.GetProperty("unavailable").GetString()));
        // Готовых представлений у выключенной таблицы нет (G4, issue #1097): настраивать нечего, строк нет.
        Assert.Empty(described.GetProperty("views").EnumerateArray());

        // В перечне таблиц выключенного модуля нет: представления скрыты, но не удалены (AUTH-19).
        var list = await client.GetFromJsonAsync<JsonElement>("/api/tables");
        Assert.DoesNotContain(list.EnumerateArray(), t => t.GetProperty("address").GetString() == "costs.invoices");
    }

    private async Task<HttpClient> AdminAsync()
    {
        var email = $"tables_{Guid.NewGuid():N}@test.local";
        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "Тест", EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, "Admin")).Succeeded);
        }

        var client = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
