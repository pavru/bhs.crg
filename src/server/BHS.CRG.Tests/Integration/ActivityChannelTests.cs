using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Activity;
using BHS.CRG.Domain.Activity;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Канал записи журнала и отбор по нему (ТЗ CORE-28.1, CORE-28, issue #1336).
///
/// <para>Канал проверяется на живом адресе, а не вызовом службы: его определяет автор журнала по
/// контексту запроса, и вызов <c>IActivityLog</c> из теста шёл бы мимо этого контекста.</para>
/// </summary>
[Collection("Integration")]
public class ActivityChannelTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private const string Password = "Passw0rd!";

    [Fact]
    public async Task Действие_в_интерфейсе_несёт_канал_ui()
    {
        var (admin, _) = await SignInAsync(SystemRoles.Admin);
        var (_, targetId) = await SignInAsync(SystemRoles.IdEngineer);

        (await admin.PutAsJsonAsync($"/api/users/{targetId}/roles",
            new { roles = new[] { SystemRoles.Admin } })).EnsureSuccessStatusCode();

        var row = Assert.Single(await RowsAsync(admin, $"action={ActivityActions.UserRoleChanged.Code}"));
        Assert.Equal("ui", row.GetProperty("channel").GetString());
    }

    [Fact]
    public async Task Действие_системы_несёт_канал_system()
    {
        // Вне запроса — как запись состава модулей при старте.
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IActivityLog>()
                .RecordAsync(ActivityActions.ModulesChanged, after: "id");

        var (admin, _) = await SignInAsync(SystemRoles.Admin);
        var row = Assert.Single(await RowsAsync(admin, $"action={ActivityActions.ModulesChanged.Code}"));
        Assert.Equal("system", row.GetProperty("channel").GetString());
        Assert.Equal("Система", row.GetProperty("actorName").GetString());
    }

    /// <summary>
    /// Отбор идёт по рядам, а общее число — по тому же отбору: «ещё N» на экране не врёт о числе действий
    /// агента. Записи разных каналов заведены импортом — единственным путём, где канал приходит снаружи.
    /// </summary>
    [Fact]
    public async Task Отбор_по_каналу_отдаёт_только_его_ряды_и_считает_по_тому_же_отбору()
    {
        var actor = Guid.NewGuid();
        using (var scope = fixture.Services.CreateScope())
        {
            var journal = scope.ServiceProvider.GetRequiredService<IActivityLog>();
            await journal.ImportAsync(
            [
                Record(actor, ActivityChannel.Mcp, "агентом 1"),
                Record(actor, ActivityChannel.Mcp, "агентом 2"),
                Record(actor, ActivityChannel.Ui, "в интерфейсе"),
            ]);
        }

        var (admin, _) = await SignInAsync(SystemRoles.Admin);
        var page = await PageAsync(admin, $"action={ActivityActions.UserRoleChanged.Code}&channel=mcp");

        var rows = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("mcp", r.GetProperty("channel").GetString()));
        Assert.Equal(2, page.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Неизвестный_канал_даёт_400_с_перечнем()
    {
        var (admin, _) = await SignInAsync(SystemRoles.Admin);

        var response = await admin.GetAsync("/api/activity?channel=agent");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["ui", "mcp", "system"],
            body.GetProperty("channels").EnumerateArray().Select(c => c.GetString()).ToArray());
        Assert.Contains("agent", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Перечень_каналов_с_названиями_отдаёт_сервер()
    {
        // Отбор на экране строится по этому ответу, а не по списку в клиенте (ревью PR #1997).
        var (admin, _) = await SignInAsync(SystemRoles.Admin);

        var channels = await admin.GetFromJsonAsync<JsonElement>("/api/activity/channels");

        Assert.Equal(ActivityChannel.All.Select(c => (c.Code, c.Title)).ToArray(),
            channels.EnumerateArray()
                .Select(c => (c.GetProperty("code").GetString()!, c.GetProperty("title").GetString()!)).ToArray());
    }

    [Fact]
    public async Task Без_права_на_журнал_отбор_по_каналу_403()
    {
        var (engineer, _) = await SignInAsync(SystemRoles.IdEngineer);
        Assert.Equal(HttpStatusCode.Forbidden, (await engineer.GetAsync("/api/activity?channel=mcp")).StatusCode);
    }

    private static ActivityRecord Record(Guid actor, ActivityChannel channel, string after) => ActivityRecord.Create(
        ActivityActions.UserRoleChanged.Code, actor, "Иван", targetId: Guid.NewGuid().ToString(),
        after: after, id: Guid.NewGuid(), occurredAt: DateTimeOffset.UtcNow, channel: channel);

    private static async Task<JsonElement> PageAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/api/activity?{query}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<List<JsonElement>> RowsAsync(HttpClient client, string query) =>
        [.. (await PageAsync(client, query)).GetProperty("items").EnumerateArray()];

    private async Task<(HttpClient Client, Guid Id)> SignInAsync(string role)
    {
        var email = $"channel_{role.ToLowerInvariant()}_{Guid.NewGuid():N}@test.local";
        Guid id;
        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
            id = user.Id;
        }

        var client = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, id);
    }
}
