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
            AssertHidden(revision.GetProperty("schedules"), PlanModule.ScheduleRead);
        }
        Assert.DoesNotContain(offerId.ToString(), restricted.GetRawText());
        var offerReader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.OfferRead);
        Assert.Equal(HttpStatusCode.Forbidden, (await offerReader.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task После_переноса_снимок_остаётся_под_исходной_редакцией_со_своим_покрытием()
    {
        var scene = await NewAsync(host);
        var matched = await EstimateSeed.MatchedRowAsync(host, scene.Site, 100m);
        var source = await scene.RevisionAsync(
            matched with { Name = "Кабель" }, matched with { Name = "Лоток" });
        var offer = await scene.CreateAsync(source);
        var offerId = offer.GetProperty("id").GetGuid();
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
            var line = await db.OfferLines.SingleAsync(l => l.OfferId == offerId && l.Order == 2);
            db.OfferLines.Remove(line);
            await db.SaveChangesAsync();
        }
        var offerUrl = Url(offer);
        var sent = await ReadAsync(SendAsync(scene.Manager, HttpMethod.Post, $"{offerUrl}/send", Version(offer),
            new { sentOn = "2026-11-22" }));
        var accepted = await ReadAsync(SendAsync(scene.Manager, HttpMethod.Post, $"{offerUrl}/outcome", Version(sent),
            new { status = "accepted" }));
        var target = await scene.RevisionAsync(matched with { Name = "Другая работа" });
        await scene.MakeCurrentAsync(target);
        var preview = await ReadAsync(scene.Manager.GetAsync($"{offerUrl}/rebase-preview"));
        await ReadAsync(SendAsync(scene.Manager, HttpMethod.Post, $"{offerUrl}/rebase", Version(accepted),
            new { revisionId = target, fingerprint = Fingerprint(preview) }));

        var url = $"/api/plan/estimates/{scene.EstimateId}/derivatives";
        var result = await ReadAsync(scene.Manager.GetAsync(url));
        var revisions = result.GetProperty("revisions");
        Assert.Equal(source, revisions[0].GetProperty("revisionId").GetGuid());
        Assert.Equal(target, revisions[1].GetProperty("revisionId").GetGuid());
        var history = Assert.Single(revisions[0].GetProperty("offers").GetProperty("items").EnumerateArray());
        Assert.Equal(offerId, history.GetProperty("id").GetGuid());
        Assert.Equal(1, history.GetProperty("currentVersion").GetInt32());
        Assert.Equal("accepted", history.GetProperty("status").GetString());
        Assert.Equal("2026-11-22", history.GetProperty("sentOn").GetString());
        Assert.True(history.GetProperty("historical").GetBoolean());
        Assert.True(history.GetProperty("outdated").GetBoolean());
        var coverage = history.GetProperty("coverage");
        Assert.Equal(1, coverage.GetProperty("covered").GetInt32());
        Assert.Equal(2, coverage.GetProperty("total").GetInt32());
        Assert.Equal(50m, coverage.GetProperty("percent").GetDecimal());
        var draft = Assert.Single(revisions[1].GetProperty("offers").GetProperty("items").EnumerateArray());
        Assert.Equal(offerId, draft.GetProperty("id").GetGuid());
        Assert.Equal(2, draft.GetProperty("currentVersion").GetInt32());
        Assert.Equal("draft", draft.GetProperty("status").GetString());
        Assert.False(draft.GetProperty("historical").GetBoolean());
        Assert.False(draft.GetProperty("outdated").GetBoolean());
        Assert.Equal(100m, draft.GetProperty("coverage").GetProperty("percent").GetDecimal());
        Assert.DoesNotContain("\"money\"", result.GetRawText());
        Assert.DoesNotContain("\"price\"", result.GetRawText());
        Assert.DoesNotContain("\"content\"", result.GetRawText());

        var estimator = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead);
        var hidden = await ReadAsync(estimator.GetAsync(url));
        foreach (var revision in hidden.GetProperty("revisions").EnumerateArray())
            AssertHidden(revision.GetProperty("offers"), PlanModule.OfferRead);
    }

    [Fact]
    public async Task Блок_графиков_без_производных_возвращает_пустой_список()
    {
        var scene = await NewAsync(host);
        await scene.RevisionAsync(Row("Кабель"));
        var scheduler = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead, PlanModule.ScheduleRead);
        var result = await ReadAsync(scheduler.GetAsync($"/api/plan/estimates/{scene.EstimateId}/derivatives"));
        var revision = Assert.Single(result.GetProperty("revisions").EnumerateArray());
        var schedules = revision.GetProperty("schedules");
        Assert.Equal("ok", schedules.GetProperty("state").GetString());
        Assert.Empty(schedules.GetProperty("items").EnumerateArray());
        AssertHidden(revision.GetProperty("offers"), PlanModule.OfferRead);
    }

    [Fact]
    public async Task Графики_по_редакциям_считают_строки_один_раз_и_следуют_правам_чтения()
    {
        var scene = await ScheduleScenario.CreateAsync(host);
        var extraSection = await DistributionFlow.SectionAsync(host, scene.Site.Construction, "Вторая секция");
        var work = await EstimateSeed.MatchedRowAsync(host, scene.Site, 1400m);
        var first = await scene.RevisionAsync(work, work with { Name = "Удалённая работа" },
            Row("Без вида"), Row("Материал") with { Kind = EstimateRowKind.Material });
        var rows = await DistributionFlow.RowIdsAsync(host, first);
        await DistributionFlow.LinesAsync(scene.Editor, first,
            DistributionFlow.Line(rows[0], "plan", (scene.Site.Section, 700m), (extraSection, 500m)));
        var old = await scene.BuildAsync(first, "Основной график");
        var deletedWork = Assert.Single(ScheduleScenario.Works(old),
            w => ScheduleScenario.GuidOrNull(w, "sourceRowId") == rows[1]);
        using var removed = await scene.Editor.SendAsync(RateMatchFlow.With(HttpMethod.Delete,
            ScheduleScenario.WorkUrl(old, deletedWork), Version(deletedWork)));
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        await scene.ManualAsync(old);

        var second = await scene.RevisionAsync(work);
        var revisionScene = new OfferDerivativeScenario(host, scene.Site, scene.Estimate, scene.Editor);
        await revisionScene.MakeCurrentAsync(second);
        var current = await scene.BuildAsync(second, "Действующий график");
        var url = $"/api/plan/estimates/{scene.Estimate}/derivatives";
        var answer = await ReadAsync(scene.Editor.GetAsync(url));
        var revisions = answer.GetProperty("revisions");
        var shown = Assert.Single(revisions[0].GetProperty("schedules").GetProperty("items").EnumerateArray());
        Assert.Equal(ScheduleScenario.Id(old), ScheduleScenario.Id(shown));
        Assert.Equal("Основной график", shown.GetProperty("number").GetString());
        Assert.True(shown.GetProperty("outdated").GetBoolean());
        var coverage = shown.GetProperty("coverage");
        Assert.Equal(1, coverage.GetProperty("covered").GetInt32());
        Assert.Equal(2, coverage.GetProperty("total").GetInt32());
        Assert.Equal(50m, coverage.GetProperty("percent").GetDecimal());
        var latest = Assert.Single(revisions[1].GetProperty("schedules").GetProperty("items").EnumerateArray());
        Assert.Equal(ScheduleScenario.Id(current), ScheduleScenario.Id(latest));
        Assert.False(latest.GetProperty("outdated").GetBoolean());
        Assert.Equal(100m, latest.GetProperty("coverage").GetProperty("percent").GetDecimal());
        AssertHidden(revisions[0].GetProperty("offers"), PlanModule.OfferRead);
        Assert.DoesNotContain("cost", answer.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("price", answer.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("volume", answer.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("money", answer.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var estimator = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead);
        var restricted = await ReadAsync(estimator.GetAsync(url));
        foreach (var revision in restricted.GetProperty("revisions").EnumerateArray())
            AssertHidden(revision.GetProperty("schedules"), PlanModule.ScheduleRead);
        Assert.DoesNotContain(ScheduleScenario.Id(old).ToString(), restricted.GetRawText());
        var scheduler = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.ScheduleRead);
        Assert.Equal(HttpStatusCode.Forbidden, (await scheduler.GetAsync(url)).StatusCode);
    }

    private static void AssertHidden(JsonElement block, string permission)
    {
        Assert.Equal("hidden", block.GetProperty("state").GetString());
        Assert.Equal(permission, block.GetProperty("permission").GetString());
        Assert.Equal(2, block.EnumerateObject().Count());
    }
}
