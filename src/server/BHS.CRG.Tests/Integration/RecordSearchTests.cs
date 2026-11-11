using BHS.CRG.Modules.Search;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Search;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Costs.Search;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Tests.Support;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Поиск записей палитры (shell-05, задачи 1.1–1.5; ТЗ AUTH-16.1, CORE-34): <c>GET /api/search</c> отвечает
/// группами в правах спрашивающего. Хост — со счетами (<see cref="InvoiceLineHost" />, <c>id,costs</c>); без
/// <c>costs</c> — <see cref="RecordSearchWithoutCostsTests" />.
///
/// <para>Каждый тест ищет своей меткой: база общая для классов хоста, и чужие счета с тем же словом сдвигали бы
/// число найденного.</para>
/// </summary>
[Collection("Integration")]
public class RecordSearchTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    private static string Mark() => Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task Право_только_на_счета_отдаёт_группу_счетов_и_ничего_больше()
    {
        var mark = Mark();
        var (admin, _) = await SignInAsync("Admin");
        await InvoiceAsync(admin, $"{mark}-1", purpose: null);
        await SiteNamedAsync($"Стройка {mark}");
        await DataSetAsync($"Набор {mark}", CatalogScope.System, null);

        var onlyInvoices = await GrantedSignIn.WithPermissionsAsync(host, "costs.invoice.read");
        var groups = await GroupsAsync(onlyInvoices, mark);

        Assert.Equal(["invoices"], groups.Select(Code));
        // А у того, кому открыто всё, совпадение есть в трёх группах — значит, двух других нет не потому,
        // что им нечего ответить.
        Assert.Equal(["invoices", "constructions", "datasets"], (await GroupsAsync(admin, mark)).Select(Code));
    }

    [Fact]
    public async Task Без_права_на_счета_нет_ни_счёта_ни_признака_что_он_есть()
    {
        var mark = Mark();
        var (admin, _) = await SignInAsync("Admin");
        var number = $"{mark}-418";
        await InvoiceAsync(admin, number, purpose: null);

        var stranger = await GrantedSignIn.WithPermissionsAsync(host, CorePermissions.ConstructionsRead);
        var response = await stranger.GetAsync($"/api/search?q={number}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(mark, body);
        Assert.DoesNotContain("invoices", body);
        // Попросить группу счетов прямо — тоже ничего: закрытая группа не отвечает и числом.
        Assert.Empty(await GroupsAsync(stranger, number, group: "invoices"));
    }

    [Fact]
    public async Task Счёт_находится_по_словам_в_любом_порядке_и_несёт_карточку_макета()
    {
        var mark = Mark();
        var (admin, _) = await SignInAsync("Admin");
        var id = await InvoiceAsync(admin, $"{mark}-7", purpose: $"Кабель для щитовой {mark}", total: 184_320m);

        // «торг» — из имени поставщика (справочник ядра), «щитов» — из назначения, метка — из номера.
        var group = (await GroupsAsync(admin, $"щитов торг {mark}", group: "invoices")).Single();
        var hit = group.GetProperty("items").EnumerateArray().Single();

        Assert.Equal("СчётНаОплату", group.GetProperty("recordType").GetString());
        Assert.Equal("costs", group.GetProperty("module").GetString());
        Assert.Equal(id, hit.GetProperty("id").GetGuid());
        Assert.Equal($"№ {mark}-7 · ООО «Кабель-Торг»", hit.GetProperty("title").GetString());
        Assert.Equal($"Кабель для щитовой {mark}", hit.GetProperty("detail").GetString());
        Assert.Equal(184_320m, hit.GetProperty("amount").GetDecimal());
        // «ё» и «е» не различаются, регистр — тоже.
        Assert.Single((await GroupsAsync(admin, $"ЩИТОВОЙ {mark}", group: "invoices")).Single()
            .GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Много_совпадений_пять_записей_и_признак_есть_ещё()
    {
        var mark = Mark();
        var (admin, _) = await SignInAsync("Admin");
        for (var i = 0; i < 7; i++) await InvoiceAsync(admin, $"{mark}-{i}", purpose: null);

        var group = (await GroupsAsync(admin, mark, group: "invoices")).Single();

        Assert.Equal(5, group.GetProperty("items").GetArrayLength());
        Assert.True(group.GetProperty("more").GetBoolean());

        await InvoiceAsync(admin, $"{mark}x-1", purpose: null);
        var one = (await GroupsAsync(admin, $"{mark}x", group: "invoices")).Single();
        Assert.False(one.GetProperty("more").GetBoolean());
    }

    [Fact]
    public async Task Стройка_и_набор_данных_несут_код_и_место()
    {
        var mark = Mark();
        var (admin, _) = await SignInAsync("Admin");
        var site = await SiteNamedAsync($"ЖК Северный {mark}", externalCode: $"1С-{mark}");
        var (_, sections) = await SiteAsync($"Корпус {mark}", "Раздел");
        await DataSetAsync($"Кабельный журнал {mark}", CatalogScope.Section, sections[0]);

        var constructions = (await GroupsAsync(admin, $"1с-{mark}", group: "constructions")).Single();
        var found = constructions.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(site, found.GetProperty("id").GetGuid());
        Assert.Equal($"1С-{mark}", found.GetProperty("detail").GetString());
        Assert.Equal("Стройка", constructions.GetProperty("recordType").GetString());

        var dataSet = (await GroupsAsync(admin, $"журнал {mark}", group: "datasets")).Single()
            .GetProperty("items").EnumerateArray().Single();
        Assert.StartsWith($"Корпус {mark}", dataSet.GetProperty("place").GetString());
    }

    [Fact]
    public async Task Ворота_адреса_вход_и_отказы_вызывающему()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync("/api/search?q=счёт")).StatusCode);

        var (admin, _) = await SignInAsync("Admin");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/search?q=с")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/search?q=%20%20")).StatusCode);
        var unknown = await admin.GetAsync("/api/search?q=счёт&group=nothing");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("nothing", (await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    /// <summary>
    /// Отказ одной группы не роняет остальные (задача 1.5): поставщик, бросивший исключение, и поставщик,
    /// не уложившийся в предел, отвечают отказом у своей группы, а счета и стройки — найденным.
    /// </summary>
    [Fact]
    public async Task Отказ_и_таймаут_поставщика_названы_у_своей_группы()
    {
        var mark = Mark();
        var (admin, _) = await SignInAsync("Admin");
        await InvoiceAsync(admin, $"{mark}-1", purpose: null);

        await using var probed = host.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            // Синтетический поставщик от имени включённого модуля — тот же приём, что у сторожей каталога
            // контрактов: вклад модуля в контракт со многими поставщиками один, а у «id» его нет.
            services.AddModuleContract<IRecordSearch, FailingSearch>("id");
            services.AddSingleton(new RecordSearchLimits(PerGroup: TimeSpan.FromMilliseconds(300)));
        }));
        var client = await GrantedSignIn.WithPermissionsAsync(probed, "costs.invoice.read", CorePermissions.ConstructionsRead);

        var groups = (await GroupsAsync(client, mark)).ToDictionary(Code);

        Assert.Single(groups["invoices"].GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, groups["invoices"].GetProperty("refused").ValueKind);
        Assert.Contains("ошибка сервера", groups[FailingSearch.Broken].GetProperty("refused").GetString());
        // Текст исключения — в журнал, а не человеку: в нём бывают имена таблиц.
        Assert.DoesNotContain("probe_secret", groups[FailingSearch.Broken].GetProperty("refused").GetString());
        Assert.Empty(groups[FailingSearch.Broken].GetProperty("items").EnumerateArray());
        Assert.Contains("нет ответа", groups[FailingSearch.Slow].GetProperty("refused").GetString());
    }

    /// <summary>
    /// Карточки по идентификатору — для «Недавних» (<c>shell-08</c>): тот же контракт, те же права. Чужая
    /// запись и удалённая неотличимы — обеих нет.
    /// </summary>
    [Fact]
    public async Task Карточки_счетов_в_правах_спрашивающего()
    {
        var (admin, _) = await SignInAsync("Admin");
        var id = await InvoiceAsync(admin, $"{Mark()}-1", purpose: null);

        using var scope = host.Services.CreateScope();
        var search = scope.ServiceProvider.GetRequiredService<IModuleContracts>().GetAll<IRecordSearch>()
            .Single(p => p.Module == "costs").Contract;
        var reader = ModuleReader.Of(Guid.NewGuid(), "проверка", ["costs.invoice.read"]);

        var shown = Assert.IsType<ContractAnswer<IReadOnlyList<RecordSearchHit>>.Shown>(
            await search.CardsAsync(CostsRecordSearch.Group, [id, Guid.NewGuid()], reader, default));
        Assert.Equal([id], shown.Value.Select(c => c.Id));

        var hidden = Assert.IsType<ContractAnswer<IReadOnlyList<RecordSearchHit>>.Hidden>(
            await search.CardsAsync(CostsRecordSearch.Group, [id], ModuleReader.Of(Guid.NewGuid(), "чужой", []), default));
        Assert.Equal("costs.invoice.read", hidden.Permission);
    }

    // ── Помощники ─────────────────────────────────────────────────────────────

    private static string Code(JsonElement group) => group.GetProperty("code").GetString()!;

    private static async Task<List<JsonElement>> GroupsAsync(HttpClient client, string q, string? group = null)
    {
        var url = $"/api/search?q={Uri.EscapeDataString(q)}" + (group is null ? "" : $"&group={group}");
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groups").EnumerateArray()];
    }

    private static async Task<Guid> InvoiceAsync(HttpClient client, string number, string? purpose, decimal? total = null)
    {
        var requisites = new Dictionary<string, object?>
        {
            ["Номер"] = number,
            ["Дата"] = "2026-11-12",
            ["Поставщик"] = Reference(supplier),
            ["Назначение"] = purpose,
        };
        if (total is { } sum) requisites["Итого"] = sum;
        var response = await client.PostAsJsonAsync("/api/costs/invoices", new { requisites });
        await OkAsync(response);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<Guid> SiteNamedAsync(string name, string? externalCode = null)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var site = Domain.Documents.Construction.Create(name, Guid.NewGuid());
        if (externalCode is not null) site.SetExternalId("1С", externalCode);
        db.Constructions.Add(site);
        await db.SaveChangesAsync();
        return site.Id;
    }

    private async Task DataSetAsync(string name, CatalogScope scope, Guid? scopeId)
    {
        using var services = host.Services.CreateScope();
        var db = services.ServiceProvider.GetRequiredService<AppDbContext>();
        db.DataSetFiles.Add(DataSetFile.Create(name, DataSetFormat.Csv, $"datasets/{Guid.NewGuid()}.csv", scope, scopeId));
        await db.SaveChangesAsync();
    }
}

/// <summary>Поставщик-проба: одна группа падает, другая не слушает предела.</summary>
public sealed class FailingSearch : IRecordSearch
{
    public const string Broken = "probe-broken";
    public const string Slow = "probe-slow";

    public IReadOnlyList<RecordSearchGroup> Groups { get; } =
    [
        new(Broken, "Сломанная", "ПробаСломанная", CorePermissions.ConstructionsRead, RecordSearchArchive.None),
        new(Slow, "Медленная", "ПробаМедленная", CorePermissions.ConstructionsRead, RecordSearchArchive.None),
    ];

    public async Task<ContractAnswer<RecordSearchHits>> SearchAsync(
        string group, RecordSearchQuery query, ModuleReader reader, CancellationToken ct)
    {
        if (group == Broken) throw new InvalidOperationException("таблица probe_secret недоступна");
        // Токен не слушает нарочно: предел обязан держать ответ и тогда.
        await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None);
        return ContractAnswer.Shown(new RecordSearchHits([], false));
    }

    public Task<ContractAnswer<IReadOnlyList<RecordSearchHit>>> CardsAsync(
        string group, IReadOnlyCollection<Guid> ids, ModuleReader reader, CancellationToken ct) =>
        Task.FromResult(ContractAnswer.Shown<IReadOnlyList<RecordSearchHit>>([]));
}
