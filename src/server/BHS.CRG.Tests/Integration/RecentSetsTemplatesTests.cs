using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Recent;
using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Templates;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Search;
using BHS.CRG.Tests.Support;
using MediatR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Комплекты и шаблоны в «Недавних» Главной (shell-09, задачи 2.1–2.6; ТЗ ID-13, CORE-25.3): карточки
/// отдаёт модуль <c>id</c> в правах спрашивающего, версии одного шаблона — одной строкой, удаление версии
/// переносит отметки на текущую.
///
/// <para>Ломается так: комплект без права «Строек» стоит строкой «не показано» навсегда; шаблон, у которого
/// очистка удалила открытую версию, пропадает с Главной, хотя жив; три версии одного шаблона — три строки.</para>
/// </summary>
[Collection("Integration")]
public sealed class RecentSetsTemplatesTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    private readonly InvoiceLineHost host = host;
    private const string Set = IdRecordTypes.DocumentSet;
    private const string Shape = IdRecordTypes.Template;
    private const string Invoice = "СчётНаОплату";

    // ── Комплекты ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Карточка_комплекта_называет_раздел_и_стройку_и_открывается_под_стройкой()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (site, sets) = await SetsAsync("Раздел ЭМ", 1);

        await TouchAsync(admin, Set, sets[0]);

        var card = Assert.Single(await ItemsAsync(admin));
        Assert.Equal(IdRecordSearch.SetsGroup, card.GetProperty("group").GetString());
        Assert.Equal("Комплекты", card.GetProperty("groupTitle").GetString());
        Assert.Equal("Комплект 1", card.GetProperty("title").GetString());
        Assert.Equal("Раздел ЭМ", card.GetProperty("detail").GetString());
        Assert.StartsWith("Корпус", card.GetProperty("place").GetString());
        Assert.Equal(site, card.GetProperty("opens").GetProperty("under").GetGuid());
        Assert.Equal(JsonValueKind.Null, card.GetProperty("opens").GetProperty("id").ValueKind);
    }

    [Fact]
    public async Task Три_комплекта_и_два_счёта_пять_строк()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (_, sets) = await SetsAsync("Раздел", 3);
        List<Guid> invoices = [await CreateAsync(admin), await CreateAsync(admin)];
        foreach (var id in sets) await TouchAsync(admin, Set, id);
        foreach (var id in invoices) await TouchAsync(admin, Invoice, id);

        var items = await ItemsAsync(admin);
        Assert.Equal(5, items.Count);
        // У прежних типов карточка не изменилась: «открыть под» у счёта не бывает.
        Assert.All(items.Where(i => i.GetProperty("recordType").GetString() == Invoice),
            i => Assert.Equal(JsonValueKind.Null, i.GetProperty("opens").ValueKind));
    }

    [Fact]
    public async Task Удалённый_комплект_не_отдаётся()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (_, sets) = await SetsAsync("Раздел", 2);
        foreach (var id in sets) await TouchAsync(admin, Set, id);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.DocumentSets.Where(d => d.Id == sets[0]).ExecuteDeleteAsync();
        }

        Assert.Equal([sets[1]], (await ItemsAsync(admin)).Select(Id));
    }

    [Fact]
    public async Task Без_права_на_документы_нет_ни_комплектов_ни_шаблонов_а_стройка_есть()
    {
        var (_, sets) = await SetsAsync("Раздел", 1);
        var (_, versions) = await TemplateAsync(1);
        var (site, _) = await SiteAsync("Стройка");
        var client = await GrantedSignIn.WithPermissionsAsync(host,
            CorePermissions.ConstructionsRead, "id.config.edit");

        await TouchAsync(client, Set, sets[0]);
        await TouchAsync(client, Shape, versions[0]);
        await TouchAsync(client, "Стройка", site);

        Assert.Equal([site], (await ItemsAsync(client)).Select(Id));
    }

    [Fact]
    public async Task Без_права_экрана_группа_пропадает_целиком_и_без_отказа()
    {
        var (_, sets) = await SetsAsync("Раздел", 1);
        var (_, versions) = await TemplateAsync(1);
        var noSites = await GrantedSignIn.WithPermissionsAsync(host, "id.document.read", "id.config.edit");
        var noTemplates = await GrantedSignIn.WithPermissionsAsync(host, "id.document.read", CorePermissions.ConstructionsRead);
        foreach (var client in new[] { noSites, noTemplates })
        {
            await TouchAsync(client, Set, sets[0]);
            await TouchAsync(client, Shape, versions[0]);
        }

        // ItemsAsync сверяет и пустоту отказов: группа скрыта, а не отказала.
        Assert.Equal([versions[0]], (await ItemsAsync(noSites)).Select(Id));
        Assert.Equal([sets[0]], (await ItemsAsync(noTemplates)).Select(Id));
    }

    // ── Шаблоны ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Карточка_шаблона_по_старой_версии_ведёт_на_новую()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (type, versions) = await TemplateAsync(1);
        await TouchAsync(admin, Shape, versions[0]);
        var next = await NewVersionAsync(versions[0]);

        var card = Assert.Single(await ItemsAsync(admin));
        Assert.Equal(IdRecordSearch.TemplatesGroup, card.GetProperty("group").GetString());
        Assert.Equal(versions[0], Id(card));
        Assert.Equal("v2", card.GetProperty("detail").GetString());
        Assert.Equal("АОСР", card.GetProperty("place").GetString());
        Assert.Equal(type, card.GetProperty("opens").GetProperty("under").GetGuid());
        Assert.Equal(next, card.GetProperty("opens").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Версии_одного_шаблона_одной_строкой_с_поздним_открытием()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (_, versions) = await TemplateAsync(1);
        await TouchAsync(admin, Shape, versions[0]);
        var next = await NewVersionAsync(versions[0]);
        var (_, sets) = await SetsAsync("Раздел", 1);
        await TouchAsync(admin, Set, sets[0]);
        await TouchAsync(admin, Shape, next);

        var items = await ItemsAsync(admin);
        Assert.Equal([next, sets[0]], items.Select(Id));
    }

    [Fact]
    public async Task Две_действующие_версии_каждое_чтение_ведёт_на_большую()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (type, versions) = await TemplateAsync(1);
        Guid second;
        using (var scope = host.Services.CreateScope())
        {
            // Накопленные до #1921 данные: вторая действующая с большим номером.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var first = await db.Templates.AsNoTracking().SingleAsync(t => t.Id == versions[0]);
            var copy = Template.Restore(Guid.NewGuid(), type, first.Name, "x", 2, isActive: true, isDefault: false,
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(-1));
            db.Templates.Add(copy);
            await db.SaveChangesAsync();
            second = copy.Id;
        }
        await TouchAsync(admin, Shape, versions[0]);

        for (var i = 0; i < 2; i++)
            Assert.Equal(second, Assert.Single(await ItemsAsync(admin)).GetProperty("opens").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Абстрактный_тип_документа_карточки_не_даёт()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (type, versions) = await TemplateAsync(1);
        await TouchAsync(admin, Shape, versions[0]);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.DocumentTypes.SingleAsync(t => t.Id == type)).SetAbstract(true);
            await db.SaveChangesAsync();
        }

        Assert.Empty(await ItemsAsync(admin));
    }

    [Fact]
    public async Task Удалили_отмеченную_версию_шаблон_остаётся_и_ведёт_на_текущую()
    {
        var (first, firstUser) = await SignInAsync("Admin");
        var (both, bothUser) = await SignInAsync("Admin");
        var (_, versions) = await TemplateAsync(1);
        await TouchAsync(first, Shape, versions[0]);
        await TouchAsync(both, Shape, versions[0]);
        var next = await NewVersionAsync(versions[0]);
        await TouchAsync(both, Shape, next);

        await SendAsync(new DeleteTemplateCommand(versions[0]));

        Assert.Equal([next], await StoredAsync(firstUser));
        Assert.Equal([next], await StoredAsync(bothUser));
        foreach (var client in new[] { first, both })
            Assert.Equal(next, Assert.Single(await ItemsAsync(client)).GetProperty("opens").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Удалили_все_версии_шаблона_нет()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (_, versions) = await TemplateAsync(1);
        var invoice = await CreateAsync(admin);
        await TouchAsync(admin, Shape, versions[0]);
        await TouchAsync(admin, Invoice, invoice);

        await SendAsync(new DeleteTemplateCommand(versions[0]));

        Assert.Equal([invoice], (await ItemsAsync(admin)).Select(Id));
    }

    // ── Отказ поставщика ──────────────────────────────────────────────────────

    [Fact]
    public async Task Отказ_поставщика_id_назван_у_его_группы_а_счета_отдаются()
    {
        var (admin, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(admin);
        await using var probed = host.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            // Поставщик модуля «id» подменён падающим: тот же приём, что у отказа групп поиска.
            services.SwapModuleContract<IRecordSearch, BrokenIdCards>("id")));
        var client = await GrantedSignIn.WithPermissionsAsync(probed, "costs.invoice.read", "id.document.read");
        await TouchAsync(client, Invoice, invoice);
        await TouchAsync(client, Set, Guid.NewGuid());

        var body = await BodyAsync(client);
        Assert.Equal([invoice], body.GetProperty("items").EnumerateArray().Select(Id));
        var refused = Assert.Single(body.GetProperty("refused").EnumerateArray());
        Assert.Equal(IdRecordSearch.SetsGroup, refused.GetProperty("group").GetString());
        Assert.Contains("ошибка сервера", refused.GetProperty("reason").GetString());
    }

    // ── Помощники ─────────────────────────────────────────────────────────────

    private static Guid Id(JsonElement item) => item.GetProperty("id").GetGuid();

    private static async Task TouchAsync(HttpClient client, string recordType, Guid id) =>
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync("/api/account/recent", new { recordType, id })).StatusCode);

    private static async Task<JsonElement> BodyAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/account/recent");
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<List<JsonElement>> ItemsAsync(HttpClient client)
    {
        var body = await BodyAsync(client);
        Assert.Empty(body.GetProperty("refused").EnumerateArray());
        return [.. body.GetProperty("items").EnumerateArray()];
    }

    private async Task<List<Guid>> StoredAsync(Guid userId)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().UserRecentRecords
            .Where(r => r.UserId == userId).Select(r => r.RecordId).ToListAsync();
    }

    /// <summary>Стройка «Корпус …» с разделом и комплектами «Комплект 1…n» в нём.</summary>
    private async Task<(Guid Site, Guid[] Sets)> SetsAsync(string section, int count)
    {
        var (site, sections) = await SiteAsync("Корпус", section);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sets = Enumerable.Range(1, count).Select(i => DocumentSet.Create(sections[0], $"Комплект {i}")).ToList();
        db.DocumentSets.AddRange(sets);
        await db.SaveChangesAsync();
        return (site, [.. sets.Select(s => s.Id)]);
    }

    /// <summary>Свой тип документа и шаблон «АОСР — основной» в нём.</summary>
    private async Task<(Guid Type, Guid[] Versions)> TemplateAsync(int count)
    {
        Guid typeId;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var type = DocumentType.Create("АОСР", $"AOSR_{Guid.NewGuid():N}", DocumentTypeKind.Document, null,
                JsonDocument.Parse("""{"fields":[]}"""), TypeOwner.Core, TypeVisibility.Shared);
            db.DocumentTypes.Add(type);
            await db.SaveChangesAsync();
            typeId = type.Id;
        }
        var first = await SendAsync(new CreateTemplateCommand(typeId, "АОСР — основной", "= АОСР"));
        List<Guid> versions = [first.Id];
        for (var i = 1; i < count; i++) versions.Add(await NewVersionAsync(versions[^1]));
        return (typeId, [.. versions]);
    }

    private async Task<Guid> NewVersionAsync(Guid from) =>
        (await SendAsync(new UpdateTemplateCommand(from, "= АОСР, правка"))).Template.Id;

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }

    private async Task SendAsync(IRequest request)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }
}

/// <summary>Поставщик-проба на месте модуля <c>id</c>: группы те же, карточки падают.</summary>
public sealed class BrokenIdCards : IRecordSearch
{
    public IReadOnlyList<RecordSearchGroup> Groups => IdRecordSearch.Declared;

    public Task<ContractAnswer<RecordSearchHits>> SearchAsync(
        string group, RecordSearchQuery query, ModuleReader reader, CancellationToken ct) =>
        Task.FromResult(ContractAnswer.Shown(new RecordSearchHits([], false)));

    public Task<ContractAnswer<IReadOnlyList<RecordSearchHit>>> CardsAsync(
        string group, IReadOnlyCollection<Guid> ids, ModuleReader reader, CancellationToken ct) =>
        throw new InvalidOperationException("таблица probe_secret недоступна");
}
