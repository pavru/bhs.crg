using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Адреса конъюнктурного анализа: цены — только с правом на стоимость, удаление с версией, загрузка и удаление под
/// правом правки (change <c>plan-11-conjuncture</c>, ТЗ PLAN-9, PLAN-2, AUTH-8, AUTH-9).
/// </summary>
[Collection(PlanOnlyCollection.Name)]
public class ConjunctureEndpointsTests(PlanOnlyHost host)
{
    /// <summary>Поля денег анализа — ни одно из них не приходит без <c>plan.estimate.cost</c>.</summary>
    private static readonly string[] Money = ["cost", "price", "priceWithVat", "selectedPrice"];

    /// <summary>Без права на стоимость в ответе нет ни одного денежного поля, а корень называет скрытое.</summary>
    [Fact]
    public async Task Без_права_на_стоимость_нет_ни_одной_цены()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var editor = await EstimateImportFlow.EditorAsync(host);
        var attachment = await ConjunctureFlow.AttachedAsync(editor, estimate, ConjunctureFlow.Ka);
        var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead);

        var page = await ConjunctureFlow.ItemsAsync(reader, ConjunctureFlow.Id(attachment), limit: 1000);

        var hidden = Assert.Single(page.GetProperty("hidden").EnumerateArray());
        Assert.Equal("cost", hidden.GetProperty("code").GetString());
        Assert.Equal(PlanModule.EstimateCost, hidden.GetProperty("permission").GetString());
        var items = page.GetProperty("items");
        Assert.True(items.GetArrayLength() > 0);
        Assert.Empty(Names(items).Where(Money.Contains));
        // Позиции и поставщики на месте: без денег анализ читается, а не пустеет.
        Assert.False(string.IsNullOrWhiteSpace(items[0].GetProperty("offers")[0].GetProperty("supplier").GetString()));
        Assert.Equal("min", items[0].GetProperty("selection").GetString());

        var list = await reader.GetFromJsonAsync<JsonElement>($"/api/plan/estimates/{estimate}/conjuncture");
        Assert.Empty(Names(list).Where(Money.Contains));
    }

    /// <summary>Удаление: без <c>If-Match</c> — 400, с устаревшей версией — 409 и анализ на месте; с версией — 204 и файл ушёл.</summary>
    [Fact]
    public async Task Удаление_называет_версию()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var client = await EstimateImportFlow.EditorAsync(host);
        var attachment = await ConjunctureFlow.AttachedAsync(client, estimate, ConjunctureFlow.Ka);
        var path = $"/api/plan/conjuncture/{ConjunctureFlow.Id(attachment)}";

        var file = await client.GetAsync(path + "/file");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal(GrandSmetaSamples.Gsfx(ConjunctureFlow.Ka), await file.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(ConjunctureFlow.Delete(path, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(ConjunctureFlow.Delete(path, "1"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path + "/items")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.SendAsync(ConjunctureFlow.Delete(path, ConjunctureFlow.Version(attachment)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path + "/items")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path + "/file")).StatusCode);
        var list = await client.GetFromJsonAsync<JsonElement>($"/api/plan/estimates/{estimate}/conjuncture");
        Assert.Equal(0, list.GetArrayLength());
    }

    /// <summary>Только чтение: загрузить, принять и удалить нельзя — 403; читать можно.</summary>
    [Fact]
    public async Task Только_чтение_не_загружает_и_не_удаляет()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var editor = await EstimateImportFlow.EditorAsync(host);
        var attachment = await ConjunctureFlow.AttachedAsync(editor, estimate, ConjunctureFlow.Ka);
        var parsed = await ConjunctureFlow.ParsedAsync(editor, estimate, ConjunctureFlow.Ka);
        var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead);
        var path = $"/api/plan/conjuncture/{ConjunctureFlow.Id(attachment)}";

        Assert.Equal(HttpStatusCode.Forbidden,
            (await ConjunctureFlow.PostUploadAsync(reader, estimate, GrandSmetaSamples.Gsfx(ConjunctureFlow.Ka), "КА.gsfx")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await ConjunctureFlow.AcceptAsync(reader, estimate, EstimateImportFlow.Id(parsed))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await reader.SendAsync(ConjunctureFlow.Delete(path, ConjunctureFlow.Version(attachment)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(path + "/items")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(path + "/file")).StatusCode);
    }

    /// <summary>Имена всех полей ответа — вглубь.</summary>
    private static IEnumerable<string> Names(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().SelectMany(p => Names(p.Value).Prepend(p.Name)),
        JsonValueKind.Array => e.EnumerateArray().SelectMany(Names),
        _ => [],
    };
}

/// <summary>На умолчательном хосте модуль <c>plan</c> выключен: адреса приложений — отказ выключенного модуля (501).</summary>
[Collection("Integration")]
public class ConjunctureWithoutModuleTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task Модуль_выключен_501()
    {
        var client = fixture.CreateClient();
        var id = Guid.NewGuid();
        using var form = ConjunctureFlow.Form([1], "КА.gsfx");
        foreach (var request in new[]
                 {
                     new HttpRequestMessage(HttpMethod.Get, $"/api/plan/estimates/{id}/conjuncture"),
                     new HttpRequestMessage(HttpMethod.Get, $"/api/plan/estimates/{id}/volume-statements"),
                     new HttpRequestMessage(HttpMethod.Post, $"/api/plan/estimates/{id}/conjuncture") { Content = form },
                     new HttpRequestMessage(HttpMethod.Post, $"/api/plan/estimates/{id}/conjuncture/{id}/accept"),
                     new HttpRequestMessage(HttpMethod.Get, $"/api/plan/conjuncture/{id}/items"),
                     ConjunctureFlow.Delete($"/api/plan/conjuncture/{id}", "1"),
                     new HttpRequestMessage(HttpMethod.Get, $"/api/plan/volume-statements/{id}/lines"),
                     ConjunctureFlow.Delete($"/api/plan/volume-statements/{id}", "1"),
                 })
        {
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
            Assert.Equal("plan", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("module").GetString());
        }
    }
}
