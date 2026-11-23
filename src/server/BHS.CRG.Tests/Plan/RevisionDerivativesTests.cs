using System.Net;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static BHS.CRG.Tests.Plan.OfferDerivativeScenario;

namespace BHS.CRG.Tests.Plan;

[Collection(PlanOnlyCollection.Name)]
public class RevisionDerivativesTests(PlanOnlyHost host)
{
    [Fact]
    public async Task Покрытие_считает_сопоставленные_работы_своей_редакции_и_видно_по_правам()
    {
        var scene = await NewAsync(host);
        var matched = await EstimateSeed.MatchedRowAsync(host, scene.Site, 100m);
        var first = await scene.RevisionAsync([
            .. Enumerable.Range(1, 40).Select(n => matched with { Name = $"Работа {n}", NumberInSection = n.ToString() }),
            Row("Несопоставленная"),
            Row("Материал") with { Kind = EstimateRowKind.Material },
        ]);
        var offer = await scene.CreateAsync(first);
        var offerId = offer.GetProperty("id").GetGuid();
        // Сохранённое частичное КП: 30 из 40 сопоставленных работ, материал и несопоставленная строка.
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
            var excluded = await db.OfferLines.Where(l => l.OfferId == offerId && l.Order > 30 && l.Order <= 40).ToListAsync();
            Assert.Equal(10, excluded.Count);
            db.OfferLines.RemoveRange(excluded);
            await db.SaveChangesAsync();
        }
        await ReadAsync(SendAsync(scene.Manager, HttpMethod.Post, $"{Url(offer)}/send", Version(offer),
            new { sentOn = "2026-11-23" }));
        var second = await scene.RevisionAsync(Row("Работа новой редакции без сопоставления"));
        await scene.MakeCurrentAsync(second);
        await scene.CreateAsync(second);

        var url = $"/api/plan/estimates/{scene.EstimateId}/derivatives";
        var result = await ReadAsync(scene.Manager.GetAsync(url));
        Assert.Equal(scene.EstimateId, result.GetProperty("estimateId").GetGuid());
        var revisions = result.GetProperty("revisions");
        var old = revisions[0];
        Assert.Equal(first, old.GetProperty("revisionId").GetGuid());
        var mine = Assert.Single(old.GetProperty("offers").GetProperty("items").EnumerateArray());
        Assert.Equal(offerId, mine.GetProperty("id").GetGuid());
        Assert.Equal(offer.GetProperty("number").GetString(), mine.GetProperty("number").GetString());
        Assert.Equal("sent", mine.GetProperty("status").GetString());
        Assert.Equal("2026-11-23", mine.GetProperty("sentOn").GetString());
        Assert.True(mine.GetProperty("outdated").GetBoolean());
        var coverage = mine.GetProperty("coverage");
        Assert.Equal(30, coverage.GetProperty("covered").GetInt32());
        Assert.Equal(40, coverage.GetProperty("total").GetInt32());
        Assert.Equal(75m, coverage.GetProperty("percent").GetDecimal());

        var current = revisions[1];
        Assert.Equal(second, current.GetProperty("revisionId").GetGuid());
        var latest = Assert.Single(current.GetProperty("offers").GetProperty("items").EnumerateArray());
        Assert.False(latest.GetProperty("outdated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, latest.GetProperty("coverage").GetProperty("percent").ValueKind);
        Assert.DoesNotContain("price", result.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("money", result.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var estimator = await AccessProbe.SignInAsync(host, "Estimator", "plan16");
        var restricted = await ReadAsync(estimator.GetAsync(url));
        foreach (var revision in restricted.GetProperty("revisions").EnumerateArray())
        {
            AssertHidden(revision.GetProperty("offers"), PlanModule.OfferRead);
            AssertHidden(revision.GetProperty("schedules"), "plan.schedule.read");
        }
        Assert.DoesNotContain(offerId.ToString(), restricted.GetRawText());
        var offerReader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.OfferRead);
        Assert.Equal(HttpStatusCode.Forbidden, (await offerReader.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Блок_графиков_пуст_у_читателя_до_Plan_19()
    {
        var scene = await NewAsync(host);
        await scene.RevisionAsync(Row("Кабель"));
        // План-18 ещё не регистрирует право в этой сборке. Даём код тестовой роли, как будущему читателю.
        var scheduler = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead, "plan.schedule.read");
        var result = await ReadAsync(scheduler.GetAsync($"/api/plan/estimates/{scene.EstimateId}/derivatives"));
        var revision = Assert.Single(result.GetProperty("revisions").EnumerateArray());
        var schedules = revision.GetProperty("schedules");
        Assert.Equal("ok", schedules.GetProperty("state").GetString());
        Assert.Empty(schedules.GetProperty("items").EnumerateArray());
        AssertHidden(revision.GetProperty("offers"), PlanModule.OfferRead);
    }

    private static void AssertHidden(JsonElement block, string permission)
    {
        Assert.Equal("hidden", block.GetProperty("state").GetString());
        Assert.Equal(permission, block.GetProperty("permission").GetString());
        Assert.Equal(2, block.EnumerateObject().Count());
    }
}
