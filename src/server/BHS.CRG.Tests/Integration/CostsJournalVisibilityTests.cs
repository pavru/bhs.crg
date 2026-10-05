using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Activity;
using BHS.CRG.Application.Activity;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Costs.Endpoints;
using BHS.CRG.Modules.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Запись модуля в журнале действий видна тому, кому открыт модуль, а запись о счёте — только с правом
/// на счета (задача H1 этапа 2, issue #1104).
///
/// <para>До правки журнал отдавал всё по одному <c>core.audit.read</c>: человек без модуля читал, какой
/// счёт какого поставщика завели и оплатили. Проверяется через живой адрес и на трёх читателях — без
/// модуля, с модулем без права на счета, с правом, — потому что правило собирается из трёх мест
/// (объявление действия, каталог, запрос), и потеряйся оно в любом, остальные остались бы зелёными.</para>
/// </summary>
[Collection("Integration")]
public class CostsJournalVisibilityTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    /// <summary>
    /// Действия модуля счетов, которым хватает открытого модуля, — с причиной. Остальные обязаны
    /// назвать право чтения: новое действие, забывшее его, роняет перепись ниже.
    /// </summary>
    private static readonly Dictionary<string, string> OpenOnPurpose = new()
    {
        ["costs.article.created"] = "справочник статей открыт: название статьи видно и без права на счета",
        ["costs.article.renamed"] = "то же",
        ["costs.article.deleted"] = "то же",
    };

    [Fact]
    public async Task Журнал_показывает_записи_модуля_только_тому_кому_они_открыты()
    {
        var (admin, _) = await SignInAsync("Admin");
        await ArticleAsync(admin, "Видимость");
        await CreateAsync(admin);

        var auditor = await ReaderAsync("core.audit.read");
        var storekeeper = await ReaderAsync("core.audit.read", "costs.waybill.read");

        // Без модуля: ни записей, ни строк отбора — и общее число не выдаёт, сколько скрыто.
        Assert.Equal(0, await TotalAsync(auditor, InvoiceActions.Created));
        Assert.Equal(0, await TotalAsync(auditor, InvoiceActions.ArticleCreated));
        Assert.DoesNotContain(await ActionsAsync(auditor), code => code.StartsWith("costs."));
        var page = await PageAsync(auditor, "?take=200");
        Assert.DoesNotContain(page.GetProperty("items").EnumerateArray(),
            r => r.GetProperty("action").GetString()!.StartsWith("costs."));
        Assert.Equal(await CoreTotalAsync(), page.GetProperty("total").GetInt32());

        // Модуль открыт, права на счета нет: справочник виден, счета — нет.
        Assert.True(await TotalAsync(storekeeper, InvoiceActions.ArticleCreated) > 0);
        Assert.Equal(0, await TotalAsync(storekeeper, InvoiceActions.Created));
        var offered = await ActionsAsync(storekeeper);
        Assert.Contains(InvoiceActions.ArticleCreated.Code, offered);
        Assert.DoesNotContain(InvoiceActions.Created.Code, offered);

        // С правом — всё на месте: правило закрывает, а не теряет.
        Assert.True(await TotalAsync(admin, InvoiceActions.Created) > 0);
        Assert.Contains(InvoiceActions.Created.Code, await ActionsAsync(admin));
    }

    [Fact]
    public void Каждое_действие_модуля_счетов_названо_закрытым_или_открытым_с_причиной()
    {
        var unnamed = new InvoiceActions().Actions
            .Where(a => a.ReadPermission is null && !OpenOnPurpose.ContainsKey(a.Code))
            .Select(a => a.Code);

        Assert.True(!unnamed.Any(),
            "Действие журнала без права чтения видно каждому, у кого есть хоть одно право модуля: " +
            string.Join(", ", unnamed) + ". Назовите право (ReadPermission) или причину в OpenOnPurpose.");
        Assert.All(OpenOnPurpose.Keys, code =>
            Assert.Contains(new InvoiceActions().Actions, a => a.Code == code && a.ReadPermission is null));
    }

    [Theory]
    [InlineData("core.invoice.read")]     // право другого владельца
    [InlineData("costs.invoice.peek")]    // такого права модуль не объявлял
    public void Каталог_отказывает_действию_с_правом_которого_модуль_не_объявлял(string permission)
    {
        IModuleActivityActions[] declarations = [new Closed(permission)];

        var refusal = Assert.Throws<InvalidOperationException>(() => new ActivityActionCatalog(declarations,
            host.Services.GetRequiredService<ModuleRegistry>(),
            host.Services.GetRequiredService<PermissionCatalog>()));

        Assert.Contains("не увидел бы никто", refusal.Message);
    }

    [Fact]
    public void Запись_неизвестного_владельца_закрыта_а_не_показана_всем()
    {
        var visible = ActivityVisibility.Of(["core", "costs"], ["costs.invoice.paid"]);

        Assert.True(visible.Shows("core.user.created"));
        Assert.True(visible.Shows("costs.article.created"));
        Assert.False(visible.Shows("costs.invoice.paid"));
        // Модуль, которого на экземпляре нет, — и владелец, чьё имя лишь начинается так же.
        Assert.False(visible.Shows("ghost.thing.done"));
        Assert.False(visible.Shows("corex.thing.done"));
        Assert.False(ActivityVisibility.Of([], []).Shows("core.user.created"));
        Assert.True(ActivityVisibility.Whole.Shows("ghost.thing.done"));
    }

    private sealed class Closed(string permission) : IModuleActivityActions
    {
        public IReadOnlyList<ModuleActivityAction> Actions => [new("costs.probe.closed", "Проба", permission)];
    }

    /// <summary>Читатель с ролью ровно из названных прав: у системных ролей такого состава нет.</summary>
    private async Task<HttpClient> ReaderAsync(params string[] permissions)
    {
        var (admin, _) = await SignInAsync("Admin");
        var created = await admin.PostAsJsonAsync("/api/roles", new
        {
            title = $"Читатель журнала {Guid.NewGuid():N}",
            summary = "Проверка видимости записей модуля",
            permissions,
        });
        await OkAsync(created);
        var role = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString()!;
        return (await SignInAsync(role)).Client;
    }

    private static async Task<JsonElement> PageAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync("/api/activity" + query);
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<int> TotalAsync(HttpClient client, ModuleActivityAction action)
    {
        var page = await PageAsync(client, $"?action={action.Code}");
        var total = page.GetProperty("total").GetInt32();
        // Число и страница обязаны говорить одно: ноль записей при ненулевом числе выдавал бы скрытое.
        Assert.Equal(total > 0, page.GetProperty("items").GetArrayLength() > 0);
        return total;
    }

    private static async Task<string[]> ActionsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/activity/actions");
        await OkAsync(response);
        return [.. (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .Select(a => a.GetProperty("code").GetString()!)];
    }

    private async Task<int> CoreTotalAsync()
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IActivityLog>()
            .CountAsync(ActivityVisibility.Of([ActivityActionCatalog.CoreOwner], []));
    }
}
