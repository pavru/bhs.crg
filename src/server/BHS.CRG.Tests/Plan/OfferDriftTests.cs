using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;
using static BHS.CRG.Tests.Plan.OfferDerivativeScenario;

namespace BHS.CRG.Tests.Plan;

[Collection(PlanOnlyCollection.Name)]
public class OfferDriftTests(PlanOnlyHost host)
{
    [Fact]
    public async Task Каталог_пометок_КП_читается_с_номерами_в_списке_и_карточке()
    {
        var scene = await NewAsync(host);
        var first = await scene.RevisionAsync(Row("Кабель"));
        var offer = await scene.CreateAsync(first);
        var version = Version(offer);

        await AssertDrift("none", 1, 1);
        var second = await scene.RevisionAsync(Row("Кабель", cost: 200m));
        await AssertDrift("newer-revision", 1, 2);
        await scene.MakeCurrentAsync(second);
        await AssertDrift("current-changed", 2, 2);

        async Task AssertDrift(string code, int current, int latest)
        {
            var detail = await ReadAsync(scene.Manager.GetAsync(Url(offer)));
            Assert.Equal(version, Version(detail));
            Assert.Equal(120m, detail.GetProperty("money").GetProperty("total").GetDecimal());
            var page = await ReadAsync(scene.Manager.GetAsync($"/api/plan/offers?estimateId={scene.EstimateId}"));
            var listed = Assert.Single(page.GetProperty("items").EnumerateArray());
            foreach (var dto in new[] { detail, listed })
            {
                var drift = dto.GetProperty("drift");
                Assert.Equal(code, drift.GetProperty("code").GetString());
                Assert.Contains("ред. 1", drift.GetProperty("text").GetString());
                Assert.Equal(first, drift.GetProperty("source").GetProperty("id").GetGuid());
                Assert.Equal(current, drift.GetProperty("current").GetProperty("number").GetInt32());
                Assert.Equal(latest, drift.GetProperty("latest").GetProperty("number").GetInt32());
            }
        }
    }

    [Fact]
    public async Task КП_по_прайс_листу_не_переносится_и_сохраняет_цену_и_единицу()
    {
        var scene = await NewAsync(host);
        var c = await EstimateSeed.ClassifierAsync(host);
        var priceEditor = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.PriceListEdit);
        var priceUrl = $"/api/plan/pricelist/{c.WorkType}";
        var price = await ReadAsync(SendAsync(priceEditor, HttpMethod.Put, priceUrl, null,
            new { validFrom = "2026-11-01", price = 17.35m }));
        var offer = await ReadAsync(scene.Manager.PostAsJsonAsync("/api/plan/offers/from-pricelist", new
        {
            constructionId = scene.Site.Construction, date = "2026-11-23",
            lines = new[] { new { workTypeId = c.WorkType, volume = 3m } },
        }), HttpStatusCode.Created);
        await ReadAsync(SendAsync(priceEditor, HttpMethod.Put, priceUrl, Version(price),
            new { validFrom = "2026-11-01", price = 900m }));
        var target = await scene.RevisionAsync(Row("Кабель"));
        var url = Url(offer);
        using var preview = await scene.Manager.GetAsync($"{url}/rebase-preview?revisionId={target}");
        Assert.Equal(HttpStatusCode.Conflict, preview.StatusCode);
        using var refused = await SendAsync(scene.Manager, HttpMethod.Post, $"{url}/rebase", Version(offer),
            new { revisionId = target, fingerprint = "unknown" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var detail = await ReadAsync(scene.Manager.GetAsync(url));
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("drift").ValueKind);
        Assert.Equal(Version(offer), Version(detail));
        var line = detail.GetProperty("lines")[0];
        Assert.Equal(c.Unit, line.GetProperty("priceListUnitId").GetGuid());
        Assert.Equal("м", line.GetProperty("unit").GetString());
        Assert.Equal(17.35m, line.GetProperty("price").GetDecimal());
        Assert.Equal(52.05m, line.GetProperty("amount").GetDecimal());
        var page = await ReadAsync(scene.Manager.GetAsync($"/api/plan/offers?constructionId={scene.Site.Construction}"));
        Assert.False(Assert.Single(page.GetProperty("items").EnumerateArray()).TryGetProperty("drift", out _));
    }
}
