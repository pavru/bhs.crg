using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Api.Auth;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Plan;

[Collection(PlanOnlyCollection.Name)]
public class PriceListEndpointsTests(PlanOnlyHost host)
{
    private const string PriceList = "/api/plan/pricelist";
    private const string Offers = "/api/plan/offers";
    private static readonly DateOnly January = new(2026, 1, 1);
    private static readonly DateOnly June = new(2026, 6, 1);
    private static readonly DateOnly July = new(2026, 7, 1);

    [Fact]
    public async Task Действующая_цена_и_история_на_дату()
    {
        var manager = await ManagerAsync();
        var work = await WorkAsync("Прокладка");
        await PutAsync(manager, work, January, 10m);
        await PutAsync(manager, work, July, 20m);

        var before = await ReadPriceAsync(manager, work, January.AddDays(-1));
        Assert.Equal(JsonValueKind.Null, before.GetProperty("effective").ValueKind);
        var between = await ReadPriceAsync(manager, work, June);
        Assert.Equal(10m, between.GetProperty("effective").GetProperty("price").GetDecimal());
        var after = await ReadPriceAsync(manager, work, July);
        Assert.Equal(20m, after.GetProperty("effective").GetProperty("price").GetDecimal());
        Assert.Equal("м", after.GetProperty("unit").GetString());
        Assert.Equal([20m, 10m], after.GetProperty("history").EnumerateArray().Select(e => e.GetProperty("price").GetDecimal()));
    }

    [Fact]
    public async Task Правка_и_снятие_цены_требуют_права_и_актуальной_версии()
    {
        var manager = await ManagerAsync();
        var work = await WorkAsync("Монтаж");
        var entry = await PutAsync(manager, work, January, 10m);
        var edit = $"{PriceList}/{work}";
        var delete = $"{PriceList}/entries/{entry.GetProperty("id").GetGuid()}";
        var body = new { ValidFrom = January, Price = 12m };

        var estimator = await AccessProbe.SignInAsync(host, "Estimator", "plan14");
        Assert.Equal(HttpStatusCode.Forbidden, (await estimator.GetAsync(PriceList)).StatusCode);
        var offerEditor = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.OfferEdit);
        Assert.Equal(HttpStatusCode.Forbidden, (await offerEditor.PutAsJsonAsync(edit, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await offerEditor.DeleteAsync(delete)).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PutAsJsonAsync(edit, body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.DeleteAsync(delete)).StatusCode);
        var changed = await PutAsync(manager, work, January, 12m, Version(entry));
        Assert.NotEqual(Version(entry), Version(changed));
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(manager, HttpMethod.Put, edit, Version(entry), body)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(manager, HttpMethod.Delete, delete, Version(entry))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(manager, HttpMethod.Delete, delete, Version(changed))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(manager, HttpMethod.Put, edit, Version(changed), body)).StatusCode);
    }

    [Fact]
    public async Task Цена_удерживает_вид_работы_от_удаления()
    {
        var work = await WorkAsync("Вид с ценой");
        var manager = await ManagerAsync();
        var first = await PutAsync(manager, work, January, 10m);
        var second = await PutAsync(manager, work, July, 20m);
        var admin = await AccessProbe.SignInAsync(host, SystemRoles.Admin, "plan14");
        var version = (await new WorkTypeSeed(host).SendAsync(new GetCommonDataEntryQuery(work)))!.Version;
        var delete = $"/api/work-types/{work}";
        var refused = await JsonAsync(await SendAsync(admin, HttpMethod.Delete, delete, version), HttpStatusCode.Conflict);
        Assert.Contains("строки прайс-листа с этим видом работы", refused.GetProperty("error").GetString());
        Assert.Contains("2", refused.GetProperty("error").GetString());
        foreach (var entry in new[] { first, second })
            Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(manager, HttpMethod.Delete,
                $"{PriceList}/entries/{entry.GetProperty("id").GetGuid()}", Version(entry))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(admin, HttpMethod.Delete, delete, version)).StatusCode);
    }

    [Fact]
    public async Task КП_показывает_отсутствующую_цену_и_неполный_итог()
    {
        var manager = await ManagerAsync();
        var site = await EstimateSeed.SiteAsync(host);
        var priced = await WorkAsync("Бесплатная работа");
        var missing = await WorkAsync("Будущая цена");
        await PutAsync(manager, priced, January, 0m);
        await PutAsync(manager, missing, July, 30m);

        var offer = await CreateOfferAsync(manager, site.Construction, priced, missing);
        Assert.Equal("pricelist", offer.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, offer.GetProperty("revision").ValueKind);
        Assert.Equal(JsonValueKind.Null, offer.GetProperty("drift").ValueKind);
        Assert.False(offer.GetProperty("canBuildSchedule").GetBoolean());
        Assert.Equal("КП не из сметы", offer.GetProperty("scheduleReason").GetString());
        Assert.False(offer.TryGetProperty("cost", out _));
        Assert.False(offer.TryGetProperty("hidden", out _));

        var lines = offer.GetProperty("lines");
        Assert.Equal(0m, lines[0].GetProperty("price").GetDecimal());
        Assert.Equal(January, lines[0].GetProperty("priceSource").GetProperty("validFrom").Deserialize<DateOnly>());
        Assert.Equal(JsonValueKind.Null, lines[1].GetProperty("price").ValueKind);
        Assert.Equal(JsonValueKind.Null, lines[1].GetProperty("amount").ValueKind);
        Assert.Equal("none", lines[1].GetProperty("priceSource").GetProperty("kind").GetString());
        Assert.Equal("no-price", Assert.Single(lines[1].GetProperty("marks").EnumerateArray()).GetProperty("code").GetString());
        Assert.True(offer.GetProperty("money").GetProperty("incomplete").GetBoolean());

        var list = await JsonAsync(await manager.GetAsync($"{Offers}?constructionId={site.Construction}"), HttpStatusCode.OK);
        Assert.True(Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("money").GetProperty("incomplete").GetBoolean());
    }

    [Fact]
    public async Task Цена_заморожена_до_пересчёта_черновика_с_предпросмотром()
    {
        var manager = await ManagerAsync();
        var site = await EstimateSeed.SiteAsync(host);
        var work = await WorkAsync("Прокладка лотка");
        var other = await WorkAsync("Шкаф");
        var first = await PutAsync(manager, work, January, 10m);
        await PutAsync(manager, other, January, 5m);
        var offer = await CreateOfferAsync(manager, site.Construction, work, other);
        var id = offer.GetProperty("id").GetGuid();
        var url = $"{Offers}/{id}";
        var reprice = $"{url}/reprice-from-pricelist";
        await PutAsync(manager, work, January, 20m, Version(first));

        Assert.Equal(10m, (await ReadOffer()).GetProperty("lines")[0].GetProperty("price").GetDecimal());
        offer = await JsonAsync(await SendAsync(manager, HttpMethod.Put, $"{url}/markups", Version(offer),
            new { markup = 10m }), HttpStatusCode.OK);
        Assert.Equal(11m, offer.GetProperty("lines")[0].GetProperty("price").GetDecimal());
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsJsonAsync(reprice, new { })).StatusCode);

        var preview = await JsonAsync(await SendAsync(manager, HttpMethod.Post, reprice, Version(offer),
            new { preview = true }), HttpStatusCode.OK);
        var change = Assert.Single(preview.GetProperty("changes").EnumerateArray());
        Assert.Equal(offer.GetProperty("lines")[0].GetProperty("id").GetGuid(), change.GetProperty("lineId").GetGuid());
        Assert.Equal(11m, change.GetProperty("previousPrice").GetDecimal());
        Assert.Equal(22m, change.GetProperty("price").GetDecimal());
        Assert.Equal(11m, (await ReadOffer()).GetProperty("lines")[0].GetProperty("price").GetDecimal());

        var applied = await JsonAsync(await SendAsync(manager, HttpMethod.Post, reprice, Version(offer), new { }), HttpStatusCode.OK);
        var updated = applied.GetProperty("offer");
        Assert.NotEqual(Version(offer), Version(updated));
        Assert.Equal(55m, updated.GetProperty("money").GetProperty("total").GetDecimal());
        Assert.Single(applied.GetProperty("changes").EnumerateArray());
        Assert.Equal(22m, (await ReadOffer()).GetProperty("lines")[0].GetProperty("price").GetDecimal());
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(manager, HttpMethod.Post, reprice, Version(offer), new { })).StatusCode);
        var sent = await JsonAsync(await SendAsync(manager, HttpMethod.Post, $"{url}/send", Version(updated),
            new { sentOn = June }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(manager, HttpMethod.Post, reprice, Version(sent), new { })).StatusCode);

        async Task<JsonElement> ReadOffer() => await JsonAsync(await manager.GetAsync(url), HttpStatusCode.OK);
    }

    private async Task<Guid> WorkAsync(string name)
    {
        var classifier = await EstimateSeed.ClassifierAsync(host);
        var seed = new WorkTypeSeed(host);
        using var scope = host.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<DomainObject>>();
        var unit = await repo.GetByIdAsync(classifier.Unit) ?? throw new InvalidOperationException();
        return (await seed.RecordAsync(classifier.WorkTypeClass, name, WorkTypeSeed.WorkTypeData(
            $"02.{Guid.NewGuid():N}", name, unit))).Id;
    }

    private Task<HttpClient> ManagerAsync() => AccessProbe.SignInAsync(host, "ProjectManager", "plan14");

    private static async Task<JsonElement> ReadPriceAsync(HttpClient client, Guid work, DateOnly date) =>
        Assert.Single((await JsonAsync(await client.GetAsync($"{PriceList}?date={date:yyyy-MM-dd}"), HttpStatusCode.OK))
            .GetProperty("items").EnumerateArray(), item => item.GetProperty("workTypeId").GetGuid() == work);

    private static async Task<JsonElement> PutAsync(HttpClient client, Guid work, DateOnly date, decimal price, string? version = null) =>
        await JsonAsync(await SendAsync(client, HttpMethod.Put, $"{PriceList}/{work}", version, new { validFrom = date, price }),
            HttpStatusCode.OK);

    private static async Task<JsonElement> CreateOfferAsync(HttpClient client, Guid site, params Guid[] works) =>
        await JsonAsync(await client.PostAsJsonAsync($"{Offers}/from-pricelist", new
        {
            constructionId = site, date = June, lines = works.Select(workTypeId => new { workTypeId, volume = 2m }),
        }), HttpStatusCode.Created);

    private static string Version(JsonElement value) => value.GetProperty("version").GetString()!;

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, string? version, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", version);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Ожидалось {status}, ответ {response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<JsonElement>(body);
    }
}

[Collection(CostsOnlyCollection.Name)]
public class PriceListDisabledModuleTests(CostsOnlyHost host)
{
    [Fact]
    public async Task Адреса_отвечают_отказом_выключенного_модуля()
    {
        var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.NotImplemented, (await client.GetAsync("/api/plan/pricelist")).StatusCode);
        Assert.Equal(HttpStatusCode.NotImplemented,
            (await client.PutAsJsonAsync($"/api/plan/pricelist/{Guid.NewGuid()}", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotImplemented,
            (await client.DeleteAsync($"/api/plan/pricelist/entries/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotImplemented, (await client.PostAsJsonAsync("/api/plan/offers/from-pricelist", new { })).StatusCode);
    }
}
