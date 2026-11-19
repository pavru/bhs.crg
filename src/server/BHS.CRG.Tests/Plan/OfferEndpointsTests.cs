using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Коммерческое предложение из редакции (change <c>plan-13-offers</c>, PR 1, ТЗ PLAN-10, PLAN-11, PLAN-2, PLAN-5,
/// TYPE-17, CORE-20.1): создание с фиксацией, наценки трёх уровней, НДС, деньги по правам, версия и ворота.
/// </summary>
[Collection(PlanOnlyCollection.Name)]
public class OfferEndpointsTests(PlanOnlyHost host)
{
    /// <summary>
    /// «Сделать КП из редакции» — одно действие: строки переносятся все, с несопоставленной и материалом, цены
    /// считает наценка предложения, а редакция помечена «зафиксирована: КП № …» (сценарии спеки).
    /// </summary>
    [Fact]
    public async Task КП_из_редакции_переносит_все_строки_и_фиксирует_редакцию()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var matched = await EstimateSeed.MatchedRowAsync(host, site, 300m, "Кабель") with
        {
            LsrSectionCode = "1", SourceVolume = 3m, Total = 1000m,
        };
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var revision = (await EstimateSeed.RevisionAsync(host, estimate,
            matched,
            Row("Без вида работы", "1", 2m, 500m),
            Row("Лоток", "2", 10m, 2000m) with { Kind = EstimateRowKind.Material })).Id;
        var manager = await ManagerAsync();

        var created = await manager.PostAsJsonAsync("/api/plan/offers", new { revisionId = revision, markup = 20m });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var offer = await created.Content.ReadFromJsonAsync<JsonElement>();
        var number = offer.GetProperty("number").GetString()!;
        Assert.Matches(@"^КП-\d{4}-\d{3,}$", number);
        Assert.Equal("draft", offer.GetProperty("status").GetString());
        Assert.Equal(1, offer.GetProperty("currentVersion").GetInt32());

        var lines = offer.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(["Кабель", "Без вида работы", "Лоток"], lines.Select(l => l.GetProperty("name").GetString()));
        // 1000 / 3 × 1,2 = 400,00 за единицу; сумма — 1200,00.
        Assert.Equal(400.00m, lines[0].GetProperty("price").GetDecimal());
        Assert.Equal(1200.00m, lines[0].GetProperty("amount").GetDecimal());
        Assert.Empty(lines[0].GetProperty("marks").EnumerateArray());
        Assert.Equal("unmatched", Assert.Single(lines[1].GetProperty("marks").EnumerateArray()).GetProperty("code").GetString());
        Assert.Equal("material", lines[2].GetProperty("kind").GetString());
        Assert.Empty(lines[2].GetProperty("marks").EnumerateArray());
        Assert.Equal(1200m + 600m + 2400m, offer.GetProperty("money").GetProperty("total").GetDecimal());

        using var scope = host.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<PlanDbContext>().EstimateRevisions
            .AsNoTracking().SingleAsync(r => r.Id == revision);
        Assert.True(stored.Frozen);
        Assert.Equal("offer", stored.FrozenCode);
        Assert.Equal($"КП № {number}", stored.FrozenLabel);
    }

    /// <summary>Наценка 20 % у КП, 15 % у раздела, 10 % у строки — и у каждой строки назван её уровень (сценарии спеки).</summary>
    [Fact]
    public async Task Наценки_строки_раздела_и_предложения()
    {
        var (_, _, revision) = await RevisionAsync(Row("А", "1", 1m, 100m), Row("Б", "1", 1m, 100m), Row("В", "2", 1m, 100m));
        var manager = await ManagerAsync();
        var offer = await CreateAsync(manager, revision, markup: 20m);
        var first = offer.GetProperty("lines")[0].GetProperty("id").GetGuid();

        var changed = await SendAsync(manager, HttpMethod.Put, $"/api/plan/offers/{Id(offer)}/markups", Version(offer), new
        {
            markup = 20m,
            sections = new[] { new { code = "1", markup = 15m } },
            lines = new[] { new { lineId = first, markup = 10m } },
        });

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var body = await changed.Content.ReadFromJsonAsync<JsonElement>();
        var lines = body.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal([110m, 115m, 120m], lines.Select(l => l.GetProperty("amount").GetDecimal()));
        Assert.Equal(["line", "section", "offer"], lines.Select(l => l.GetProperty("markup").GetProperty("level").GetString()));
        Assert.Equal(345m, body.GetProperty("money").GetProperty("total").GetDecimal());
        Assert.Equal(15m, Assert.Single(body.GetProperty("sections").EnumerateArray(), s => s.GetProperty("code").GetString() == "1")
            .GetProperty("markup").GetDecimal());

        // Снятые наценки раздела и строки — снова действует наценка предложения.
        var reset = await SendAsync(manager, HttpMethod.Put, $"/api/plan/offers/{Id(offer)}/markups", Version(body), new { markup = 20m });
        var after = await reset.Content.ReadFromJsonAsync<JsonElement>();
        Assert.All(after.GetProperty("lines").EnumerateArray(), l => Assert.Equal(120m, l.GetProperty("amount").GetDecimal()));
    }

    /// <summary>Итог и наценку в обход расчёта не записать: охрана записи отказывает, суммы на месте (ТЗ TYPE-17, CORE-20.2).</summary>
    [Fact]
    public async Task Итог_и_наценку_в_обход_не_записать()
    {
        var (_, _, revision) = await RevisionAsync(Row("А", "1", 1m, 100m));
        var manager = await ManagerAsync();

        var atCreation = await manager.PostAsJsonAsync("/api/plan/offers",
            new { revisionId = revision, data = new Dictionary<string, object> { ["Итого"] = 1m } });
        Assert.Equal(HttpStatusCode.BadRequest, atCreation.StatusCode);
        Assert.Contains("Итого", await atCreation.Content.ReadAsStringAsync());

        var offer = await CreateAsync(manager, revision, markup: 20m);
        var refused = await SendAsync(manager, HttpMethod.Put, $"/api/plan/offers/{Id(offer)}", Version(offer), new
        {
            date = "2026-11-16", vatMode = "none", data = new Dictionary<string, object> { ["Итого"] = 1m, ["Наценка"] = 99m },
        });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var now = await manager.GetFromJsonAsync<JsonElement>($"/api/plan/offers/{Id(offer)}");
        Assert.Equal(120m, now.GetProperty("money").GetProperty("total").GetDecimal());
        Assert.Equal(20m, now.GetProperty("markup").GetDecimal());
        Assert.False(now.GetProperty("data").TryGetProperty("Итого", out _));
    }

    /// <summary>Режим НДС у КП (В-9): «сверху» добавляет НДС к сумме строк, «в том числе» — выделяет его.</summary>
    [Fact]
    public async Task НДС_сверху_и_в_том_числе()
    {
        var (_, _, revision) = await RevisionAsync(Row("А", "1", 1m, 1000m));
        var manager = await ManagerAsync();
        var offer = await CreateAsync(manager, revision, markup: 0m);

        var onTop = await PutHeaderAsync(manager, offer, "on-top", 20m);
        Assert.Equal(HttpStatusCode.OK, onTop.StatusCode);
        var money = (await onTop.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("money");
        Assert.Equal((1000m, 200m, 1200m), (money.GetProperty("linesTotal").GetDecimal(), money.GetProperty("vat").GetDecimal(),
            money.GetProperty("total").GetDecimal()));

        var current = await manager.GetFromJsonAsync<JsonElement>($"/api/plan/offers/{Id(offer)}");
        var included = await (await PutHeaderAsync(manager, current, "included", 25m)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(200m, included.GetProperty("money").GetProperty("vat").GetDecimal());
        Assert.Equal(1000m, included.GetProperty("money").GetProperty("total").GetDecimal());

        var noRate = await PutHeaderAsync(manager, included, "on-top", null);
        Assert.Equal(HttpStatusCode.BadRequest, noRate.StatusCode);
    }

    /// <summary>
    /// Без <c>plan.estimate.cost</c> в ответе КП нет ни одного поля себестоимости — обходом JSON целиком, а не по ключам;
    /// есть носитель <c>hidden</c>. С правом — себестоимость строки из сметы и маржа (ТЗ PLAN-11, PLAN-2, D4).
    /// </summary>
    [Fact]
    public async Task Себестоимость_в_КП_только_с_правом_на_стоимость()
    {
        const decimal secretTotal = 7531.86m;
        var (_, _, revision) = await RevisionAsync(Row("А", "1", 2m, secretTotal));
        var offer = await CreateAsync(await ManagerAsync(), revision, markup: 10m);

        var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.OfferRead);
        var raw = await reader.GetStringAsync($"/api/plan/offers/{Id(offer)}");
        using (var doc = JsonDocument.Parse(raw))
        {
            Assert.DoesNotContain(Keys(doc.RootElement), k => k is "cost" or "margin");
            Assert.DoesNotContain(Numbers(doc.RootElement), n => n == secretTotal);
            var hidden = Assert.Single(doc.RootElement.GetProperty("hidden").EnumerateArray());
            Assert.Equal("cost", hidden.GetProperty("code").GetString());
            Assert.Equal(PlanModule.EstimateCost, hidden.GetProperty("permission").GetString());
            // Цены продажи — на месте: 7531,86 / 2 × 1,1 = 4142,52 за единицу.
            Assert.Equal(4142.52m, doc.RootElement.GetProperty("lines")[0].GetProperty("price").GetDecimal());
        }

        // Сторож обязан срабатывать: с правом себестоимость в ответе есть.
        var estimator = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.OfferRead, PlanModule.EstimateCost);
        var full = await estimator.GetFromJsonAsync<JsonElement>($"/api/plan/offers/{Id(offer)}");
        Assert.False(full.TryGetProperty("hidden", out _));
        var cost = full.GetProperty("lines")[0].GetProperty("cost");
        Assert.Equal(secretTotal, cost.GetProperty("total").GetDecimal());
        Assert.Equal(8285.04m - secretTotal, cost.GetProperty("margin").GetDecimal());
    }

    /// <summary>Правка без <c>If-Match</c> — 400, с устаревшей версией — 409, правка не записана (ТЗ AUTH-8).</summary>
    [Fact]
    public async Task Правка_называет_версию()
    {
        var (_, _, revision) = await RevisionAsync(Row("А", "1", 1m, 100m));
        var manager = await ManagerAsync();
        var offer = await CreateAsync(manager, revision, markup: 0m);
        var url = $"/api/plan/offers/{Id(offer)}";

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(manager, HttpMethod.Put, $"{url}/markups", null, new { markup = 5m })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(manager, HttpMethod.Delete, url, null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(manager, HttpMethod.Put, $"{url}/markups", Version(offer), new { markup = 5m })).StatusCode);

        var stale = await SendAsync(manager, HttpMethod.Put, $"{url}/markups", Version(offer), new { markup = 50m });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(5m, (await manager.GetFromJsonAsync<JsonElement>(url)).GetProperty("markup").GetDecimal());
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(manager, HttpMethod.Delete, url, Version(offer))).StatusCode);
    }

    /// <summary>
    /// Удаление черновика: КП уходит, фиксация редакции остаётся — редакция видела производную (design, «Фиксация»).
    /// </summary>
    [Fact]
    public async Task Удаление_КП_не_снимает_фиксацию()
    {
        var (_, _, revision) = await RevisionAsync(Row("А", "1", 1m, 100m));
        var manager = await ManagerAsync();
        var offer = await CreateAsync(manager, revision, markup: 0m);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(manager, HttpMethod.Delete, $"/api/plan/offers/{Id(offer)}", Version(offer))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/plan/offers/{Id(offer)}")).StatusCode);

        using var scope = host.Services.CreateScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<PlanDbContext>().EstimateRevisions.AsNoTracking()
            .SingleAsync(r => r.Id == revision)).Frozen);
    }

    /// <summary>
    /// «Сметчик» прав на КП не имеет — каждый адрес КП отвечает 403, и чтение тоже (ТЗ PLAN-2); чтение без правки не
    /// пишет.
    /// </summary>
    [Fact]
    public async Task Сметчик_и_читатель_не_пишут()
    {
        var (_, _, revision) = await RevisionAsync(Row("А", "1", 1m, 100m));
        var offer = await CreateAsync(await ManagerAsync(), revision, markup: 0m);

        var estimator = await AccessProbe.SignInAsync(host, "Estimator", "plan13");
        Assert.Equal(HttpStatusCode.Forbidden, (await estimator.GetAsync("/api/plan/offers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await estimator.GetAsync($"/api/plan/offers/{Id(offer)}")).StatusCode);

        var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.OfferRead, PlanModule.EstimateRead);
        foreach (var client in new[] { estimator, reader })
            foreach (var request in Writes(Id(offer), revision))
                Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }

    /// <summary>
    /// Заказчик — организация справочника по роли: архивная в новое КП не выбирается, живая называется в ответе
    /// (ТЗ CORE-34.4). Пробу называет перепись чтений архива.
    /// </summary>
    [Fact]
    public async Task Архивный_заказчик_не_выбирается()
    {
        var (live, archived) = await CustomersAsync();
        var (_, _, revision) = await RevisionAsync(Row("А", "1", 1m, 100m));
        var manager = await ManagerAsync();

        var refused = await manager.PostAsJsonAsync("/api/plan/offers", new { revisionId = revision, customerId = archived });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("в архиве", await refused.Content.ReadAsStringAsync());

        var offer = await CreateAsync(manager, revision, markup: 0m, customer: live);
        var customer = offer.GetProperty("customer");
        Assert.Equal(live, customer.GetProperty("id").GetGuid());
        Assert.Equal("ООО «Заказчик КП»", customer.GetProperty("name").GetString());
        Assert.False(customer.GetProperty("archived").GetBoolean());
    }

    /// <summary>КП — документ с суммами в валюте учёта: пока оно есть, валюту не сменить (ТЗ CORE-20.1).</summary>
    [Fact]
    public async Task Смена_валюты_при_КП_отказывает()
    {
        var (_, _, revision) = await RevisionAsync(Row("А", "1", 1m, 100m));
        await CreateAsync(await ManagerAsync(), revision, markup: 0m);
        var admin = await AccessProbe.SignInAsync(host, SystemRoles.Admin, "plan13");

        var refused = await admin.PutAsJsonAsync("/api/settings/company/currency", new { currency = "USD" });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var error = (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        Assert.Contains("коммерческих предложений:", error);
    }

    /// <summary>Список КП стройки — свежие первыми, с суммой и редакцией-источником.</summary>
    [Fact]
    public async Task Список_КП_стройки()
    {
        var (site, estimate, revision) = await RevisionAsync(Row("А", "1", 1m, 100m));
        var manager = await ManagerAsync();
        var first = await CreateAsync(manager, revision, markup: 0m);
        var second = await CreateAsync(manager, revision, markup: 50m);

        var list = await manager.GetFromJsonAsync<JsonElement>($"/api/plan/offers?constructionId={site.Construction}&limit=1");
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(Id(second), item.GetProperty("id").GetGuid());
        Assert.Equal(150m, item.GetProperty("money").GetProperty("total").GetDecimal());
        Assert.Equal(estimate, item.GetProperty("revision").GetProperty("estimateId").GetGuid());

        var next = list.GetProperty("next").GetString();
        var page = await manager.GetFromJsonAsync<JsonElement>($"/api/plan/offers?constructionId={site.Construction}&cursor={next}");
        Assert.Equal(Id(first), Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }

    /// <summary>Себестоимости в таблицах КП нет (ТЗ PLAN-11): ни колонки, ни свойства с её именем у сущностей КП.</summary>
    [Fact]
    public void В_таблицах_КП_нет_себестоимости()
    {
        using var scope = host.Services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<PlanDbContext>().Model;
        var columns = new[] { typeof(Offer), typeof(OfferLine), typeof(OfferSectionMarkup) }
            .SelectMany(t => model.FindEntityType(t)!.GetProperties()).Select(p => p.GetColumnName()).ToList();

        Assert.Contains("price", columns); // проверка обязана видеть колонки КП
        Assert.DoesNotContain(columns, c => c.Contains("cost", StringComparison.Ordinal) || c.Contains("base", StringComparison.Ordinal)
            || c.Contains("margin", StringComparison.Ordinal));
    }

    private static EstimateRowDraft Row(string name, string section, decimal volume, decimal total) =>
        new(name, LsrSectionCode: section, LsrSectionName: $"Раздел {section}", SourceVolume: volume, SourceUnit: "шт",
            Total: total, UnitCost: total / volume);

    private async Task<(EstimateSeed.Site Site, Guid Estimate, Guid Revision)> RevisionAsync(params EstimateRowDraft[] rows)
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        return (site, estimate, (await EstimateSeed.RevisionAsync(host, estimate, rows)).Id);
    }

    private Task<HttpClient> ManagerAsync() =>
        GrantedSignIn.WithPermissionsAsync(host, PlanModule.OfferRead, PlanModule.OfferEdit, PlanModule.EstimateRead);

    private static async Task<JsonElement> CreateAsync(HttpClient client, Guid revision, decimal markup, Guid? customer = null)
    {
        var response = await client.PostAsJsonAsync("/api/plan/offers", new { revisionId = revision, markup, customerId = customer });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> PutHeaderAsync(HttpClient client, JsonElement offer, string vatMode, decimal? vatRate) =>
        SendAsync(client, HttpMethod.Put, $"/api/plan/offers/{Id(offer)}", Version(offer),
            new { date = offer.GetProperty("date").GetString(), vatMode, vatRate });

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, string? version, object? body = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", version);
        return client.SendAsync(request);
    }

    /// <summary>Все пишущие адреса КП — с заголовком версии, чтобы отказ был от прав, а не от версии.</summary>
    private static IEnumerable<HttpRequestMessage> Writes(Guid offer, Guid revision)
    {
        HttpRequestMessage With(HttpMethod method, string url, object? body = null)
        {
            var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
            request.Headers.TryAddWithoutValidation("If-Match", "1");
            return request;
        }

        yield return With(HttpMethod.Post, "/api/plan/offers", new { revisionId = revision });
        yield return With(HttpMethod.Put, $"/api/plan/offers/{offer}", new { date = "2026-11-16", vatMode = "none" });
        yield return With(HttpMethod.Put, $"/api/plan/offers/{offer}/markups", new { markup = 1m });
        yield return With(HttpMethod.Delete, $"/api/plan/offers/{offer}");
        yield return With(HttpMethod.Post, $"/api/plan/offers/{offer}/send", new { sentOn = "2026-11-18" });
        yield return With(HttpMethod.Post, $"/api/plan/offers/{offer}/outcome", new { status = "accepted" });
        yield return With(HttpMethod.Post, $"/api/plan/offers/{offer}/versions");
    }

    /// <summary>Справочник организаций на хосте «только планирование» — живая и архивная записи.</summary>
    private async Task<(Guid Live, Guid Archived)> CustomersAsync()
    {
        await OrganizationGate.WaitAsync();
        try
        {
            var seed = new WorkTypeSeed(host);
            using var scope = host.Services.CreateScope();
            var types = scope.ServiceProvider.GetRequiredService<BHS.CRG.Application.Common.IRepository<BHS.CRG.Domain.Documents.DocumentType>>();
            var organization = (await types.FindAsync(t => t.Code == TestDirectories.OrganizationCode)).FirstOrDefault()
                ?? await seed.TypeAsync(TestDirectories.OrganizationCode, "Организация");
            await TestDirectories.AssignRolesAsync(scope.ServiceProvider);

            var live = await seed.RecordAsync(organization.Id, "ООО «Заказчик КП»");
            var archived = await seed.RecordAsync(organization.Id, $"ООО «Бывший заказчик {Guid.NewGuid():N}»");
            await seed.ArchiveAsync(archived.Id);
            return (live.Id, archived.Id);
        }
        finally
        {
            OrganizationGate.Release();
        }
    }

    private static readonly SemaphoreSlim OrganizationGate = new(1, 1);

    private static Guid Id(JsonElement offer) => offer.GetProperty("id").GetGuid();

    private static string Version(JsonElement offer) => offer.GetProperty("version").GetString()!;

    private static IEnumerable<string> Keys(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p => Keys(p.Value).Prepend(p.Name)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(Keys),
        _ => [],
    };

    private static IEnumerable<decimal> Numbers(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p => Numbers(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(Numbers),
        JsonValueKind.Number => [decimal.Parse(element.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture)],
        _ => [],
    };
}

/// <summary>Модуль «Планирование» выключен — адреса КП отвечают отказом выключенного модуля (501, ТЗ AUTH-19).</summary>
[Collection(CostsOnlyCollection.Name)]
public class OfferDisabledModuleTests(CostsOnlyHost host)
{
    [Fact]
    public async Task Адреса_КП_без_модуля_отвечают_отказом_модуля()
    {
        var response = await host.CreateClient().GetAsync("/api/plan/offers");

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Equal("plan", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("module").GetString());
    }
}
