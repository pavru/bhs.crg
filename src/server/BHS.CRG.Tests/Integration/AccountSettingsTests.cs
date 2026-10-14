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

    /// <summary>
    /// Признак готовности рабочих пространств (issue #1001): своё пространство, выбор и умолчание
    /// хранит сервер — с другого компьютера человек возвращается в то же пространство.
    /// </summary>
    [Fact]
    public async Task Своё_пространство_и_умолчание_приезжают_на_другую_машину()
    {
        var email = await CreateUserAsync();
        const string own = """[{"id":"w1","name":"Снабжение","modules":["costs","id"]}]""";
        (await (await SignInAsync(email)).PutAsJsonAsync("/api/account/settings", new Dictionary<string, string?>
        {
            ["workspaces"] = own, ["workspace"] = "module:costs", ["workspace.default"] = "own:w1",
        })).EnsureSuccessStatusCode();

        var settings = await SettingsAsync(await SignInAsync(email));

        Assert.Equal(own, settings["workspaces"]);
        Assert.Equal("module:costs", settings["workspace"]);
        Assert.Equal("own:w1", settings["workspace.default"]);
    }

    /// <summary>
    /// Список своих пространств — JSON, и каша в нём отвергается: сохранённая, она не прочиталась бы
    /// клиентом на другом компьютере, где исправить её уже нечем. Повтор имени — тоже отказ (ТЗ
    /// AUTH-16.5: имя единственное среди своих), как и пространство без модулей.
    /// </summary>
    [Theory]
    [InlineData("не json")]
    [InlineData("""{"id":"w1"}""")]
    [InlineData("""[{"id":"w1","name":"Снабжение","modules":[]}]""")]
    [InlineData("""[{"id":"w1","name":"  ","modules":["costs"]}]""")]
    [InlineData("""[{"id":"w1","name":"Снабжение","modules":["costs"]},{"id":"w2","name":"снабжение ","modules":["id"]}]""")]
    [InlineData("""[{"id":"w1","name":"А","modules":["costs"]},{"id":"w1","name":"Б","modules":["id"]}]""")]
    [InlineData("""[{"id":"w1","name":"А","modules":["costs","costs"]}]""")]
    [InlineData("""[{"id":"w1","name":"А","modules":["Costs!"]}]""")]
    [InlineData("""[{"id":"w1","name":"все доступные модули","modules":["costs"]}]""")]
    public async Task Негодный_список_пространств_отвергается(string value)
    {
        var client = await SignInAsync(await CreateUserAsync());

        var response = await client.PutAsJsonAsync("/api/account/settings",
            new Dictionary<string, string?> { ["workspaces"] = value });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await SettingsAsync(client));
    }

    /// <summary>Выбор пространства — код из трёх видов; остальное — отказ, а не тихая запись.</summary>
    [Theory]
    [InlineData("workspace", "costs")]
    [InlineData("workspace", "own:")]
    [InlineData("workspace.default", "module:")]
    [InlineData("workspace.default", "все")]
    public async Task Негодный_выбор_пространства_отвергается(string key, string value)
    {
        var client = await SignInAsync(await CreateUserAsync());

        var response = await client.PutAsJsonAsync("/api/account/settings",
            new Dictionary<string, string?> { [key] = value });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Признак готовности избранных строек (issue #1218): выбор и выключенный фильтр хранит сервер —
    /// с другого компьютера человек видит тот же отбор. Предел — ровно двадцать.
    /// </summary>
    [Fact]
    public async Task Избранные_стройки_и_выключенный_фильтр_приезжают_на_другую_машину()
    {
        var email = await CreateUserAsync();
        var twenty = string.Join(',', Enumerable.Range(0, 20).Select(_ => Guid.NewGuid().ToString()));
        (await (await SignInAsync(email)).PutAsJsonAsync("/api/account/settings", new Dictionary<string, string?>
        {
            ["favoriteConstructions"] = twenty, ["favoritesFilter"] = "off",
        })).EnsureSuccessStatusCode();

        var settings = await SettingsAsync(await SignInAsync(email));

        Assert.Equal(twenty, settings["favoriteConstructions"]);
        Assert.Equal("off", settings["favoritesFilter"]);
    }

    /// <summary>
    /// Избранное — GUID через запятую, не больше двадцати (#1217). Двадцать первая стройка, повтор,
    /// пустой элемент, пробелы и вольная форма GUID — отказ: сохранённые, они дали бы клиенту на
    /// другом компьютере список, в котором одна стройка числится дважды или не находится вовсе.
    /// Существование строек при записи НЕ проверяется — это нарочно (см. FavoriteConstructionValues).
    /// </summary>
    [Theory]
    [InlineData(21)]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(-3)]
    [InlineData(-4)]
    [InlineData(-5)]
    public async Task Негодное_избранное_отвергается(int variant)
    {
        var a = Guid.NewGuid().ToString();
        var value = variant switch
        {
            21 => string.Join(',', Enumerable.Range(0, 21).Select(_ => Guid.NewGuid().ToString())),
            -1 => $"{a},{a.ToUpperInvariant()}",
            -2 => $"{a},",
            -3 => $"{a}, {Guid.NewGuid()}",
            -4 => Guid.NewGuid().ToString("N"),
            _ => "",
        };
        var client = await SignInAsync(await CreateUserAsync());

        var response = await client.PutAsJsonAsync("/api/account/settings",
            new Dictionary<string, string?> { ["favoriteConstructions"] = value });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await SettingsAsync(client));
    }

    /// <summary>Фильтр — только «on» или «off»: иное значение клиент прочитал бы как «включён» молча.</summary>
    [Fact]
    public async Task Негодное_состояние_фильтра_отвергается()
    {
        var client = await SignInAsync(await CreateUserAsync());

        var response = await client.PutAsJsonAsync("/api/account/settings",
            new Dictionary<string, string?> { ["favoritesFilter"] = "true" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
