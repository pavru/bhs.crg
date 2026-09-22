using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Серверные настройки пользователя (issue #953, ТЗ CORE-25.3): тема и язык переживают смену
/// компьютера, потому что лежат не в браузере.
///
/// ⚠️ «Другая машина» здесь — ОТДЕЛЬНЫЙ вход тем же человеком: свой клиент, свой токен, ничего не
/// перенесено с первого. Проверять через тот же клиент бессмысленно — он отвечал бы из своей же
/// записи, и тест сходился бы сам с собой ровно так же, как сходился браузер с localStorage.
/// </summary>
[Collection("Integration")]
public class AccountSettingsTests(IntegrationTestFixture fixture)
{
    private const string Password = "Passw0rd!";

    /// <summary>Тот самый признак готовности задачи: выбор, сделанный здесь, виден там.</summary>
    [Fact]
    public async Task Тема_выбранная_на_одной_машине_приезжает_на_другую()
    {
        var email = await CreateUserAsync();
        var first = await SignInAsync(email);

        var saved = await first.PutAsJsonAsync("/api/account/settings", new { theme = "dark", locale = "de-DE" });
        saved.EnsureSuccessStatusCode();

        var second = await SignInAsync(email);
        var settings = await SettingsAsync(second);

        Assert.Equal("dark", settings["theme"]);
        Assert.Equal("de-DE", settings["locale"]);
    }

    /// <summary>
    /// Настройки — личные. Без этой проверки хранилище могло бы отвечать общей таблицей, и тема
    /// одного человека приезжала бы всем — незаметно, потому что выглядит это как работающая
    /// синхронизация.
    /// </summary>
    [Fact]
    public async Task Настройки_соседа_не_приезжают()
    {
        var mine = await SignInAsync(await CreateUserAsync());
        (await mine.PutAsJsonAsync("/api/account/settings", new { theme = "dark" })).EnsureSuccessStatusCode();

        var stranger = await SignInAsync(await CreateUserAsync());

        Assert.Empty(await SettingsAsync(stranger));
    }

    /// <summary>
    /// Частичная правка: присланный ключ меняется, остальные остаются. Полная замена набора
    /// означала бы, что экран, который знает про тему и не знает про язык, стирает язык каждым
    /// сохранением — и человек винил бы в этом «сбой», а не соседний переключатель.
    /// </summary>
    [Fact]
    public async Task Правка_одного_ключа_не_трогает_остальные()
    {
        var client = await SignInAsync(await CreateUserAsync());
        (await client.PutAsJsonAsync("/api/account/settings", new { theme = "dark", locale = "en-GB" }))
            .EnsureSuccessStatusCode();

        (await client.PutAsJsonAsync("/api/account/settings", new { theme = "light" })).EnsureSuccessStatusCode();

        var settings = await SettingsAsync(client);
        Assert.Equal("light", settings["theme"]);
        Assert.Equal("en-GB", settings["locale"]);
    }

    /// <summary>
    /// Снятие настройки — это возврат к умолчанию: строки больше нет, и «никогда не выбирал»
    /// неотличимо от «выбрал и отменил». Пустая строка вместо этого была бы выбором «ничего», и
    /// клиент показал бы тему без названия.
    /// </summary>
    [Fact]
    public async Task Снятая_настройка_исчезает_а_не_становится_пустой()
    {
        var client = await SignInAsync(await CreateUserAsync());
        (await client.PutAsJsonAsync("/api/account/settings", new { theme = "dark" })).EnsureSuccessStatusCode();

        (await client.PutAsJsonAsync("/api/account/settings", new { theme = (string?)null }))
            .EnsureSuccessStatusCode();

        Assert.DoesNotContain("theme", await SettingsAsync(client));
    }

    /// <summary>
    /// Незнакомый ключ — ОТКАЗ, а не молчаливая запись. Записанная опечатка выглядит как удавшееся
    /// сохранение и всплывает потом жалобой «настройка не приезжает»: искать будут синхронизацию, а
    /// дело в букве.
    /// </summary>
    [Fact]
    public async Task Незнакомая_настройка_отвергается_и_не_сохраняется()
    {
        var client = await SignInAsync(await CreateUserAsync());

        var response = await client.PutAsJsonAsync("/api/account/settings", new { them = "dark" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await SettingsAsync(client));
    }

    /// <summary>
    /// Значение вне объявленного списка отвергается — и вместе с ним ВЕСЬ набор. Принять половину
    /// значило бы оставить настройки в состоянии, которого человек не выбирал, и отчитаться об этом
    /// отказом: экран показал бы ошибку, а язык всё-таки сменился бы.
    /// </summary>
    [Fact]
    public async Task Недопустимое_значение_отвергает_весь_набор()
    {
        var client = await SignInAsync(await CreateUserAsync());

        var response = await client.PutAsJsonAsync("/api/account/settings",
            new { locale = "ru-RU", theme = "фиолетовая" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await SettingsAsync(client));
    }

    /// <summary>Без входа адрес не отвечает: настройки принадлежат конкретному человеку.</summary>
    [Fact]
    public async Task Без_входа_настроек_нет()
    {
        var response = await fixture.CreateClient().GetAsync("/api/account/settings");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<Dictionary<string, string>> SettingsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/account/settings");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Dictionary<string, string>>())!;
    }

    private async Task<string> CreateUserAsync()
    {
        var email = $"prefs_{Guid.NewGuid():N}@test.local";
        using var scope = fixture.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            UserName = email, Email = email, DisplayName = "Тест", EmailConfirmed = true,
        };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        return email;
    }

    /// <summary>Новый клиент и новый вход — то же, что открыть систему на другом компьютере.</summary>
    private async Task<HttpClient> SignInAsync(string email)
    {
        var client = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
