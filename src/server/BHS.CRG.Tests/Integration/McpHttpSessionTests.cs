using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Транспорт MCP по HTTP (issue #599). Сессия жила в памяти процесса, и первый вызов после паузы
/// регулярно получал 404 «Session not found»; повтор того же вызова проходил. Мост лечил это сам
/// (#438), но любой другой клиент вынужден был закладывать слепой ретрай — а для неидемпотентного
/// вызова слепой ретрай небезопасен.
///
/// Здесь проверяется само отсутствие состояния: вызов без идентификатора сессии обязан работать.
/// Тест ходит по HTTP намеренно — режим транспорта не виден ни на одном уровне ниже.
/// </summary>
[Collection("Integration")]
public class McpHttpSessionTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpClient> AuthorizedClientAsync()
    {
        var email = $"mcp_{Guid.NewGuid():N}@example.com";
        const string password = "Passw0rd!MCP";

        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                UserName = email, Email = email, DisplayName = "Агент", EmailConfirmed = true,
            };
            var created = await users.CreateAsync(user, password);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
            // Роль обязательна: с переходом на права (issue #947) пользователь БЕЗ роли не имеет
            // ни одного права и получает 403 на всём. В живой системе такой учётной записи не
            // бывает — регистрация открыта только первому администратору, остальных заводит экран
            // пользователей и всегда с ролью, — так что заводить её здесь значило бы проверять
            // состояние, которого не существует.
            Assert.True((await users.AddToRoleAsync(user, BHS.CRG.Api.Auth.SystemRoles.IdEngineer)).Succeeded);
        }

        var client = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>
    /// Инструменты вызываются БЕЗ идентификатора сессии — терять нечего, а значит и 404 «Session not
    /// found» взяться неоткуда. Раньше второй вызов без заголовка сессии не проходил вовсе.
    /// </summary>
    [Fact]
    public async Task ToolsCall_WorksWithoutSessionHeader()
    {
        var client = await AuthorizedClientAsync();

        var init = await McpTestClient.SendAsync(client, McpTestClient.Initialize());
        Assert.Equal(HttpStatusCode.OK, init.StatusCode);
        // Сервер не выдаёт идентификатор сессии: он его и не хранит.
        Assert.False(init.Headers.Contains("Mcp-Session-Id"));
        var serverInfo = (await McpTestClient.ResultOfAsync(init)).GetProperty("serverInfo");
        Assert.Equal("bhs-crg", serverInfo.GetProperty("name").GetString());
        // Версия сборки, а не константа: по ответу видно, с какой сборкой говорит клиент (#600).
        Assert.NotEqual("1.0.0", serverInfo.GetProperty("version").GetString());

        var list = await McpTestClient.SendAsync(client, new { jsonrpc = "2.0", id = 2, method = "tools/list" });
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var tools = (await McpTestClient.ResultOfAsync(list)).GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToList();
        Assert.Contains("list_constructions", tools);
    }

    /// <summary>
    /// Пауза — то самое место, где всё ломалось. Между вызовами нет ни общего состояния, ни
    /// рукопожатия: повторный вызов через любое время проходит так же, как первый.
    /// </summary>
    [Fact]
    public async Task RepeatedCall_AfterAnotherClient_StillWorks()
    {
        var first = await AuthorizedClientAsync();
        await McpTestClient.SendAsync(first, McpTestClient.Initialize());
        var listed = await McpTestClient.SendAsync(first, new { jsonrpc = "2.0", id = 2, method = "tools/list" });
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);

        // Второй клиент вообще не здоровается: в stateless-режиме это допустимо, и именно так
        // выглядит клиент, чья сессия «протухла» бы в прежнем режиме.
        var second = await AuthorizedClientAsync();
        var withoutHandshake = await McpTestClient.SendAsync(second, new { jsonrpc = "2.0", id = 1, method = "tools/list" });

        Assert.Equal(HttpStatusCode.OK, withoutHandshake.StatusCode);
        Assert.NotEmpty((await McpTestClient.ResultOfAsync(withoutHandshake)).GetProperty("tools").EnumerateArray());
    }
}
