using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Endpoints.Account;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// «Недавние» Главной (shell-08, задачи 4.2–4.3; ТЗ CORE-25.3, UI-35, AUTH-9): свой список, не длиннее
/// двадцати, повтор поднимает; при чтении — только существующие и открытые спрашивающему записи.
///
/// <para>Ломается так: удалённый черновик или счёт, закрытый правами, стоит в «Недавних» строкой с номером и
/// поставщиком — то есть адрес выдаёт то, чего человек больше видеть не вправе.</para>
/// </summary>
[Collection("Integration")]
public sealed class RecentRecordsTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    private readonly InvoiceLineHost host = host;
    private const string Invoice = "СчётНаОплату";
    private const string Site = "Стройка";

    [Fact]
    public async Task Повтор_поднимает_и_запись_в_списке_одна()
    {
        var (admin, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(admin);
        var (site, _) = await SiteAsync($"Стройка {Guid.NewGuid():N}");

        await TouchAsync(admin, Invoice, invoice);
        await TouchAsync(admin, Site, site);
        await TouchAsync(admin, Invoice, invoice);

        var items = await ItemsAsync(admin);
        Assert.Equal([invoice, site], items.Select(Id));
        var card = items[0];
        Assert.Equal("invoices", card.GetProperty("group").GetString());
        Assert.Equal("Счета", card.GetProperty("groupTitle").GetString());
        Assert.False(string.IsNullOrEmpty(card.GetProperty("title").GetString()));
        Assert.Equal("constructions", items[1].GetProperty("group").GetString());
    }

    [Fact]
    public async Task Двадцать_первая_вытесняет_самую_давнюю()
    {
        var (admin, userId) = await SignInAsync("Admin");
        var ids = Enumerable.Range(0, RecentRecordEndpoints.Limit + 1).Select(_ => Guid.NewGuid()).ToList();
        foreach (var id in ids) await TouchAsync(admin, Invoice, id);

        var kept = await StoredAsync(userId);
        Assert.Equal(RecentRecordEndpoints.Limit, kept.Count);
        Assert.DoesNotContain(ids[0], kept);
        Assert.Contains(ids[^1], kept);
    }

    [Fact]
    public async Task Чужого_списка_не_получить()
    {
        var (owner, _) = await SignInAsync("Admin");
        var (other, _) = await SignInAsync("Admin");
        await TouchAsync(owner, Invoice, await CreateAsync(owner));

        Assert.Single(await ItemsAsync(owner));
        Assert.Empty(await ItemsAsync(other));
    }

    [Fact]
    public async Task Без_входа_401()
    {
        var anonymous = host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/account/recent")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/account/recent",
            new { recordType = Invoice, id = Guid.NewGuid() })).StatusCode);
    }

    [Fact]
    public async Task Тип_без_владельца_и_пустой_идентификатор_отказ()
    {
        var (admin, userId) = await SignInAsync("Admin");
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(admin, "Опечатка", Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(admin, Invoice, Guid.Empty)).StatusCode);
        Assert.Empty(await StoredAsync(userId));
    }

    /// <summary>Сценарий спеки «Удалённый черновик»: отметка в таблице осталась, но строки нет.</summary>
    [Fact]
    public async Task Удалённый_черновик_не_отдаётся()
    {
        var (admin, _) = await SignInAsync("Admin");
        var kept = await CreateAsync(admin);
        var draft = await CreateAsync(admin);
        await TouchAsync(admin, Invoice, kept);
        await TouchAsync(admin, Invoice, draft);
        Assert.Equal([draft, kept], (await ItemsAsync(admin)).Select(Id));

        await OkAsync(await admin.DeleteAsync($"/api/costs/invoices/{draft}"));

        Assert.Equal([kept], (await ItemsAsync(admin)).Select(Id));
    }

    /// <summary>Отмечать можно что угодно своё, но показывается только открытое правами сейчас.</summary>
    [Fact]
    public async Task Счёт_вне_прав_не_отдаётся_а_стройка_отдаётся()
    {
        var (admin, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(admin);
        var (site, _) = await SiteAsync($"Стройка {Guid.NewGuid():N}");
        var (limited, _) = await SignInAsync(await RoleAsync(CorePermissions.ConstructionsRead));

        await TouchAsync(limited, Invoice, invoice);
        await TouchAsync(limited, Site, site);

        Assert.Equal([site], (await ItemsAsync(limited)).Select(Id));
    }

    [Fact]
    public async Task Отметка_версию_счёта_не_меняет()
    {
        var (admin, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(admin);
        var before = (await ReadAsync(admin, invoice)).GetProperty("version").GetString();

        await TouchAsync(admin, Invoice, invoice);

        Assert.Equal(before, (await ReadAsync(admin, invoice)).GetProperty("version").GetString());
    }

    // ── Помощники ─────────────────────────────────────────────────────────────

    private static Guid Id(JsonElement item) => item.GetProperty("id").GetGuid();

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string recordType, Guid id) =>
        client.PostAsJsonAsync("/api/account/recent", new { recordType, id });

    private static async Task TouchAsync(HttpClient client, string recordType, Guid id) =>
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(client, recordType, id)).StatusCode);

    private static async Task<List<JsonElement>> ItemsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/account/recent");
        await OkAsync(response);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(body.GetProperty("refused").EnumerateArray());
        return [.. body.GetProperty("items").EnumerateArray()];
    }

    private async Task<List<Guid>> StoredAsync(Guid userId)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().UserRecentRecords
            .Where(r => r.UserId == userId).Select(r => r.RecordId).ToListAsync();
    }

    private async Task<string> RoleAsync(params string[] permissions)
    {
        var name = $"Recent_{Guid.NewGuid():N}";
        using var scope = host.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var role = new IdentityRole<Guid>(name);
        Assert.True((await roles.CreateAsync(role)).Succeeded);
        foreach (var permission in permissions)
            Assert.True((await roles.AddClaimAsync(role, new Claim(RoleSynchronizer.PermissionClaim, permission))).Succeeded);
        return name;
    }
}

/// <summary>
/// «Недавние» при выключенном <c>costs</c> (задача 4.3): записи модуля не отдаются, но в таблице остаются —
/// включат модуль, и они вернутся. Общий хост прогона поднят без <c>costs</c>.
/// </summary>
[Collection("Integration")]
public class RecentRecordsWithoutCostsTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task Записи_выключенного_модуля_не_отдаются_и_не_стираются()
    {
        Guid site;
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var construction = Domain.Documents.Construction.Create($"Стройка {Guid.NewGuid():N}", Guid.NewGuid());
            db.Constructions.Add(construction);
            await db.SaveChangesAsync();
            site = construction.Id;
        }
        var client = await Support.GrantedSignIn.WithPermissionsAsync(fixture, CorePermissions.ConstructionsRead);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/account/recent",
            new { recordType = "Стройка", id = site })).StatusCode);
        // Тип выключенного модуля новой отметкой не принимается: владельца у него на экземпляре нет.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/account/recent",
            new { recordType = "СчётНаОплату", id = Guid.NewGuid() })).StatusCode);

        // Отметка, сделанная, пока модуль был включён.
        var invoice = Guid.NewGuid();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var userId = await db.UserRecentRecords.Where(r => r.RecordId == site).Select(r => r.UserId).SingleAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO user_recent_records ("UserId", "RecordType", "RecordId", "OpenedAt")
                VALUES ({userId}, {"СчётНаОплату"}, {invoice}, {DateTimeOffset.UtcNow})
                """);
        }

        var response = await client.GetAsync("/api/account/recent");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([site], items.Select(i => i.GetProperty("id").GetGuid()));

        using (var scope = fixture.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<AppDbContext>().UserRecentRecords
                .AnyAsync(r => r.RecordId == invoice));
    }
}
