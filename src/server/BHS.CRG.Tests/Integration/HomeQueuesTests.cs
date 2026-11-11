using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Очереди Главной (shell-08, ТЗ UI-34, AUTH-9): числа — те же, что у чипов экрана счетов, а
/// предлагаются очереди по объявленным правам — все из списка, а не любое.
/// </summary>
[Collection("Integration")]
public sealed class HomeQueuesTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    private readonly InvoiceLineHost host = host;

    private static async Task<List<JsonElement>> HomeAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/home/queues");
        await OkAsync(response);
        return [.. (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()];
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static JsonElement Card(List<JsonElement> cards, string id) =>
        cards.Single(c => c.GetProperty("module").GetString() == "costs" && c.GetProperty("id").GetString() == id);

    private static string[] Ids(List<JsonElement> cards) => [.. cards.Select(c => c.GetProperty("id").GetString()!)];

    /// <summary>
    /// Сторож «тот же счётчик»: на одном посеве число каждой карточки — поле в поле число адреса
    /// очереди экрана, и сомнение стоит у тех же очередей, что у чипа.
    /// </summary>
    [Fact]
    public async Task Числа_Главной_равны_числам_адресов_очередей()
    {
        var (admin, _) = await SignInAsync("Admin");
        await CreateAsync(admin);

        var cards = await HomeAsync(admin);
        var queues = await GetJsonAsync(admin, "/api/costs/invoices/queues");
        var parsing = await GetJsonAsync(admin, "/api/costs/invoices/queues/parsing");

        Assert.Equal(["parsing", "unrecognized", "lost", "archived"], Ids(cards));
        Assert.Equal(parsing.GetProperty("count").GetInt32(), Card(cards, "parsing").GetProperty("count").GetInt32());
        Assert.True(Card(cards, "parsing").GetProperty("count").GetInt32() > 0);
        // «Не распознано» считается по задачам распознавания, а не по ссылкам: отказ опроса его не прячет.
        Assert.Equal(JsonValueKind.Number, Card(cards, "unrecognized").GetProperty("count").ValueKind);
        foreach (var id in new[] { "lost", "archived" })
            Assert.Equal(queues.GetProperty(id).GetInt32(), Card(cards, id).GetProperty("count").GetInt32());

        Assert.Equal(queues.GetProperty("doubt").ValueKind, Card(cards, "lost").GetProperty("doubt").ValueKind);
        Assert.Equal(JsonValueKind.Null, Card(cards, "parsing").GetProperty("refused").ValueKind);
        Assert.Equal("Счета и накладные", Card(cards, "parsing").GetProperty("moduleTitle").GetString());
        Assert.True(Card(cards, "archived").GetProperty("quiet").GetBoolean());
    }

    /// <summary>Только чтение счетов — одна «Разобрать»: остальные три зовут к правке.</summary>
    [Fact]
    public async Task Только_чтение_одна_очередь()
    {
        var (reader, _) = await SignInAsync(await RoleAsync("costs.invoice.read"));
        Assert.Equal(["parsing"], Ids(await HomeAsync(reader)));
    }

    /// <summary>
    /// Правка без чтения — ничего: права очереди — ВСЕ из списка. Карточка «Удалённые записи» иначе
    /// вела бы на закрытый экран. Только накладные — тоже ничего, и это не отказ, а пустой список.
    /// </summary>
    [Fact]
    public async Task Только_накладные_ничего()
    {
        var (waybills, _) = await SignInAsync(await RoleAsync("costs.waybill.read"));
        Assert.Empty(await HomeAsync(waybills));

        var (editor, _) = await SignInAsync(await RoleAsync("costs.invoice.edit"));
        Assert.Empty(await HomeAsync(editor));
    }

    /// <summary>
    /// Отказ обратного опроса ядра — «не посчитано» у трёх очередей на нём, а «Разобрать» приходит с
    /// числом: отказ не переодет ни в ноль, ни в пропажу карточек.
    /// </summary>
    [Fact]
    public async Task Отказ_опроса_ссылок_даёт_отказ_двум_а_прочие_с_числом()
    {
        var (admin, _) = await SignInAsync("Admin");
        await CreateAsync(admin);
        await using var broken = host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IModuleReferenceTargets>();
            services.AddScoped<IModuleReferenceTargets, BrokenTargets>();
        }));
        var client = broken.CreateClient();
        client.DefaultRequestHeaders.Authorization = admin.DefaultRequestHeaders.Authorization;

        var cards = await HomeAsync(client);
        Assert.True(Card(cards, "parsing").GetProperty("count").GetInt32() > 0);
        // «Не распознано» считается по задачам распознавания, а не по ссылкам: отказ опроса его не прячет.
        Assert.Equal(JsonValueKind.Number, Card(cards, "unrecognized").GetProperty("count").ValueKind);
        foreach (var id in new[] { "lost", "archived" })
        {
            Assert.Equal(JsonValueKind.Null, Card(cards, id).GetProperty("count").ValueKind);
            Assert.False(string.IsNullOrWhiteSpace(Card(cards, id).GetProperty("refused").GetString()));
        }
    }

    private async Task<string> RoleAsync(params string[] permissions)
    {
        var name = $"Home_{Guid.NewGuid():N}";
        using var scope = host.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var role = new IdentityRole<Guid>(name);
        Assert.True((await roles.CreateAsync(role)).Succeeded);
        foreach (var permission in permissions)
            Assert.True((await roles.AddClaimAsync(role, new Claim(RoleSynchronizer.PermissionClaim, permission))).Succeeded);
        return name;
    }

    private sealed class BrokenTargets : IModuleReferenceTargets
    {
        public Task<IReadOnlyDictionary<Guid, ReferenceState>> StatesAsync(
            ReferenceTarget target, IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ReferenceState>>(new Dictionary<Guid, ReferenceState>());

        public Task<ReferenceFindings> NotPresentAsync(
            string moduleCode, bool includeArchived, CancellationToken ct = default) =>
            throw new InvalidOperationException("опрос отказал");
    }
}

/// <summary>Выключенный модуль счетов — его очередей нет, и это пустой ответ, а не ошибка (ТЗ UI-34).</summary>
[Collection("Integration")]
public sealed class HomeQueuesWithoutCostsTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task Без_модуля_счетов_пусто_без_ошибки()
    {
        var email = $"home_{Guid.NewGuid():N}@test.local";
        const string password = "Passw0rd!";
        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            if (!await roles.RoleExistsAsync("Admin")) Assert.True((await roles.CreateAsync(new IdentityRole<Guid>("Admin"))).Succeeded);
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, "Admin")).Succeeded);
        }

        var client = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/home/queues");
        response.EnsureSuccessStatusCode();
        Assert.Empty((await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
    }
}
