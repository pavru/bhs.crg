using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Plan;

[Collection(PlanOnlyCollection.Name)]
public class OfferLifecycleTests(PlanOnlyHost host)
{
    [Theory]
    [InlineData("sent")]
    [InlineData("accepted")]
    [InlineData("rejected")]
    [InlineData("no-answer")]
    public async Task Отправленное_КП_правится_только_новой_версией_история_не_меняется(string outcome)
    {
        var manager = await ManagerAsync();
        var (offer, _) = await CreateAsync(manager);
        var sent = await SendAsync(manager, offer);
        if (outcome != "sent")
            sent = await OkAsync(await WriteAsync(manager, sent, HttpMethod.Post, "/outcome", new { status = outcome }));
        Assert.Equal(outcome, sent.GetProperty("status").GetString());
        Assert.Equal("2026-11-18", sent.GetProperty("sentOn").GetString());

        Assert.Equal(HttpStatusCode.Conflict, (await HeaderAsync(manager, sent)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await WriteAsync(manager, sent, HttpMethod.Put, "/markups", new { markup = 50m })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await WriteAsync(manager, sent, HttpMethod.Delete)).StatusCode);

        var draft = await OkAsync(await WriteAsync(manager, sent, HttpMethod.Post, "/versions"));
        Assert.Equal("draft", draft.GetProperty("status").GetString());
        Assert.Equal(2, draft.GetProperty("currentVersion").GetInt32());
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("sentOn").ValueKind);
        Assert.Equal(sent.GetProperty("number").GetString(), draft.GetProperty("number").GetString());
        Assert.Equal(sent.GetProperty("lines")[0].GetProperty("id").GetGuid(), draft.GetProperty("lines")[0].GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Conflict, (await WriteAsync(manager, draft, HttpMethod.Delete)).StatusCode);

        var changed = await OkAsync(await WriteAsync(manager, draft, HttpMethod.Put, "/markups", new { markup = 50m }));
        await OkAsync(await HeaderAsync(manager, changed));
        var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.OfferRead, PlanModule.EstimateRead);
        var history = await reader.GetFromJsonAsync<JsonElement>($"{Url(offer)}/versions");
        var version = Assert.Single(history.EnumerateArray());
        Assert.Equal(1, version.GetProperty("number").GetInt32());
        Assert.Equal(outcome, version.GetProperty("status").GetString());
        Assert.Equal("2026-11-18", version.GetProperty("sentOn").GetString());
        Assert.Equal(120m, version.GetProperty("total").GetDecimal());
        var content = version.GetProperty("content");
        Assert.Equal(20m, content.GetProperty("markup").GetDecimal());
        Assert.Equal("исходное", content.GetProperty("data").GetProperty("Примечание").GetString());
        Assert.Equal(120m, Assert.Single(content.GetProperty("lines").EnumerateArray()).GetProperty("amount").GetDecimal());
        Assert.DoesNotContain(Keys(content), key => key is "cost" or "margin" or "baseTotal");
    }

    [Theory]
    [InlineData("/send")]
    [InlineData("/outcome")]
    [InlineData("/versions")]
    public async Task Переходы_требуют_своё_право_и_актуальную_версию(string action)
    {
        var manager = await ManagerAsync();
        var (offer, _) = await CreateAsync(manager);
        if (action != "/send") offer = await SendAsync(manager, offer);
        object? body = action switch
        {
            "/send" => new { sentOn = "2026-11-18" },
            "/outcome" => new { status = "accepted" },
            _ => null,
        };
        var limited = action == "/versions"
            ? await GrantedSignIn.WithPermissionsAsync(host, PlanModule.OfferRead, PlanModule.EstimateRead, PlanModule.OfferSend)
            : await GrantedSignIn.WithPermissionsAsync(host, PlanModule.OfferRead, PlanModule.EstimateRead, PlanModule.OfferEdit);
        Assert.Equal(HttpStatusCode.Forbidden, (await WriteAsync(limited, offer, HttpMethod.Post, action, body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await WriteAsync(manager, offer, HttpMethod.Post, action, body, version: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await WriteAsync(manager, offer, HttpMethod.Post, action, body, version: "0")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await WriteAsync(manager, offer, HttpMethod.Post, action, body)).StatusCode);
    }

    [Fact]
    public async Task Некорректные_переходы_и_данные_отвергаются()
    {
        var manager = await ManagerAsync();
        var (draft, _) = await CreateAsync(manager);
        Assert.Equal(HttpStatusCode.Conflict, (await WriteAsync(manager, draft, HttpMethod.Post, "/versions")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await WriteAsync(manager, draft, HttpMethod.Post, "/outcome", new { status = "accepted" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await WriteAsync(manager, draft, HttpMethod.Post, "/send", new { })).StatusCode);
        var sent = await SendAsync(manager, draft);
        Assert.Equal(HttpStatusCode.Conflict, (await WriteAsync(manager, sent, HttpMethod.Post, "/send", new { sentOn = "2026-11-19" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await WriteAsync(manager, sent, HttpMethod.Post, "/outcome", new { status = "draft" })).StatusCode);
        var accepted = await OkAsync(await WriteAsync(manager, sent, HttpMethod.Post, "/outcome", new { status = "accepted" }));
        Assert.Equal(HttpStatusCode.Conflict, (await WriteAsync(manager, accepted, HttpMethod.Post, "/outcome", new { status = "rejected" })).StatusCode);
    }

    [Fact]
    public async Task Повтор_новой_версии_не_дублирует_историю_список_от_новой_к_старой()
    {
        var manager = await ManagerAsync();
        var (offer, _) = await CreateAsync(manager);
        var sent = await SendAsync(manager, offer);
        var responses = await Task.WhenAll(WriteAsync(manager, sent, HttpMethod.Post, "/versions"),
            WriteAsync(manager, sent, HttpMethod.Post, "/versions"));
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
        var draft = await OkAsync(responses.Single(r => r.StatusCode == HttpStatusCode.OK));
        var sent2 = await SendAsync(manager, draft);
        await OkAsync(await WriteAsync(manager, sent2, HttpMethod.Post, "/versions"));
        var history = await manager.GetFromJsonAsync<JsonElement>($"{Url(offer)}/versions");
        Assert.Equal([2, 1], history.EnumerateArray().Select(v => v.GetProperty("number").GetInt32()));
    }

    [Fact]
    public async Task Версии_только_дописываются_история_не_удаляется_вместе_с_КП()
    {
        var manager = await ManagerAsync();
        var (offer, _) = await CreateAsync(manager);
        var draft = await OkAsync(await WriteAsync(manager, await SendAsync(manager, offer), HttpMethod.Post, "/versions"));
        Assert.True(typeof(ModuleRecordVersion).IsAssignableFrom(typeof(OfferVersion)));
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
            var stored = await db.OfferVersions.SingleAsync(v => v.RecordId == Id(offer));
            db.Entry(stored).Property(v => v.Content).CurrentValue = "{}";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
            db.OfferVersions.Remove(await db.OfferVersions.SingleAsync(v => v.RecordId == Id(offer)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        Assert.Equal(HttpStatusCode.Conflict, (await WriteAsync(manager, draft, HttpMethod.Delete)).StatusCode);
        Assert.Single((await manager.GetFromJsonAsync<JsonElement>($"{Url(offer)}/versions")).EnumerateArray());
    }

    [Fact]
    public async Task Статус_и_дата_видны_в_списке_КП_и_у_редакции_по_праву_КП()
    {
        var manager = await ManagerAsync();
        var (offer, estimate) = await CreateAsync(manager);
        var sent = await SendAsync(manager, offer);
        var list = await manager.GetFromJsonAsync<JsonElement>($"/api/plan/offers?estimateId={estimate}");
        var listed = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal("sent", listed.GetProperty("status").GetString());
        Assert.Equal("2026-11-18", listed.GetProperty("sentOn").GetString());
        var card = await manager.GetFromJsonAsync<JsonElement>($"/api/plan/estimates/{estimate}");
        var derived = Assert.Single(card.GetProperty("revisions")[0].GetProperty("offers").EnumerateArray());
        Assert.Equal(Id(offer), derived.GetProperty("id").GetGuid());
        Assert.Equal("sent", derived.GetProperty("status").GetString());
        Assert.Equal("2026-11-18", derived.GetProperty("sentOn").GetString());
        Assert.False(derived.TryGetProperty("money", out _));
        var estimator = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead);
        var restricted = await estimator.GetFromJsonAsync<JsonElement>($"/api/plan/estimates/{estimate}");
        Assert.False(restricted.GetProperty("revisions")[0].TryGetProperty("offers", out _));
        Assert.Equal(HttpStatusCode.Forbidden, (await estimator.GetAsync($"{Url(offer)}/versions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/plan/offers/{Guid.NewGuid()}/versions")).StatusCode);
        Assert.Equal(1, sent.GetProperty("currentVersion").GetInt32());
    }

    private Task<HttpClient> ManagerAsync() => GrantedSignIn.WithPermissionsAsync(host,
        PlanModule.EstimateRead, PlanModule.OfferRead, PlanModule.OfferEdit, PlanModule.OfferSend);

    private async Task<(JsonElement Offer, Guid Estimate)> CreateAsync(HttpClient manager)
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var revision = await EstimateSeed.RevisionAsync(host, estimate,
            new EstimateRowDraft("Работа", SourceVolume: 1m, SourceUnit: "шт", Total: 100m));
        var created = await manager.PostAsJsonAsync("/api/plan/offers", new
        {
            revisionId = revision.Id, markup = 20m, data = new Dictionary<string, string> { ["Примечание"] = "исходное" },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return ((await created.Content.ReadFromJsonAsync<JsonElement>()), estimate);
    }

    private static Task<HttpResponseMessage> HeaderAsync(HttpClient client, JsonElement offer) =>
        WriteAsync(client, offer, HttpMethod.Put, body: new { date = "2026-11-19", vatMode = "none", data = new { Примечание = "новое" } });

    private static async Task<JsonElement> SendAsync(HttpClient client, JsonElement offer) =>
        await OkAsync(await WriteAsync(client, offer, HttpMethod.Post, "/send", new { sentOn = "2026-11-18" }));

    private static Task<HttpResponseMessage> WriteAsync(HttpClient client, JsonElement offer, HttpMethod method,
        string suffix = "", object? body = null, string? version = "current")
    {
        var request = new HttpRequestMessage(method, Url(offer) + suffix)
        {
            Content = body is null ? null : JsonContent.Create(body),
        };
        if (version is not null)
            request.Headers.TryAddWithoutValidation("If-Match", version == "current" ? offer.GetProperty("version").GetString() : version);
        return client.SendAsync(request);
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Guid Id(JsonElement offer) => offer.GetProperty("id").GetGuid();
    private static string Url(JsonElement offer) => $"/api/plan/offers/{Id(offer)}";
    private static IEnumerable<string> Keys(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().SelectMany(p => Keys(p.Value).Prepend(p.Name)),
        JsonValueKind.Array => value.EnumerateArray().SelectMany(Keys),
        _ => [],
    };
}
