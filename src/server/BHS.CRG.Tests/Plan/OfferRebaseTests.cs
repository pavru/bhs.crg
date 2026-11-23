using System.Net;
using System.Text.Json;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static BHS.CRG.Tests.Plan.OfferDerivativeScenario;

namespace BHS.CRG.Tests.Plan;

[Collection(PlanOnlyCollection.Name)]
public class OfferRebaseTests(PlanOnlyHost host)
{
    [Fact]
    public async Task Предпросмотр_различает_главы_и_строки_и_переносит_только_однозначные_наценки()
    {
        var scene = await NewAsync(host);
        var first = await scene.RevisionAsync(
            Row("А") with { Number = "13" }, Row("Б", "2"), Row("В", number: "2"),
            Row("Снятая", number: "9") with { RateCode = "ГЭСН 09" },
            Row("Дубль А", "3"), Row("Дубль Б", "3"));
        var target = await scene.RevisionAsync(
            Row("Б", "2", cost: 400m), Row("В", number: "2", cost: 500m),
            Row("А", cost: 300m) with { Number = "28", SourceVolume = 2m, SourceUnit = "10 м" },
            Row("Добавленная", number: "8", cost: 800m) with { RateCode = "ГЭСН 08" }, Row("Спорная", "3", cost: 200m));
        var offer = await scene.CreateAsync(first);
        var url = Url(offer);
        var lines = offer.GetProperty("lines");
        offer = await ReadAsync(SendAsync(scene.Manager, HttpMethod.Put, $"{url}/markups", Version(offer), new
        {
            markup = 20m,
            sections = new[] { new { code = "2", markup = 15m } },
            lines = new[]
            {
                new { lineId = lines[0].GetProperty("id").GetGuid(), markup = 10m },
                new { lineId = lines[4].GetProperty("id").GetGuid(), markup = 55m },
            },
        }));

        var previewUrl = $"{url}/rebase-preview?revisionId={target}";
        var preview = await ReadAsync(scene.Manager.GetAsync(previewUrl));
        Assert.Equal(Version(offer), Version(preview));
        Assert.Equal(target, preview.GetProperty("target").GetProperty("id").GetGuid());
        Assert.DoesNotContain("\"cost\":", preview.GetRawText());
        Assert.Equal(PlanModule.EstimateCost, Assert.Single(preview.GetProperty("hidden").EnumerateArray())
            .GetProperty("permission").GetString());
        var changed = preview.GetProperty("changed").EnumerateArray().ToDictionary(change => Name(change.GetProperty("before")));
        Assert.Equal(3, changed.Count);
        var a = changed["А"];
        Assert.Equal("13", a.GetProperty("before").GetProperty("number").GetString());
        Assert.Equal(110m, Price(a.GetProperty("before")));
        Assert.Equal("28", a.GetProperty("after").GetProperty("number").GetString());
        Assert.Equal("10 м", a.GetProperty("after").GetProperty("unit").GetString());
        Assert.Equal(2m, a.GetProperty("after").GetProperty("volume").GetDecimal());
        Assert.Equal(165m, Price(a.GetProperty("after")));
        Assert.Equal(460m, Price(changed["Б"].GetProperty("after")));
        Assert.Equal(600m, Price(changed["В"].GetProperty("after")));
        Assert.Equal("Добавленная", Name(Assert.Single(preview.GetProperty("added").EnumerateArray())));
        Assert.Equal("Снятая", Name(Assert.Single(preview.GetProperty("removed").EnumerateArray())));
        var unmatched = Assert.Single(preview.GetProperty("unmatched").EnumerateArray());
        Assert.Equal(["Дубль А", "Дубль Б"], unmatched.GetProperty("before").EnumerateArray().Select(Name));
        Assert.Equal("Спорная", Name(Assert.Single(unmatched.GetProperty("after").EnumerateArray())));

        var costEditor = await GrantedSignIn.WithPermissionsAsync(host,
            PlanModule.OfferRead, PlanModule.OfferEdit, PlanModule.EstimateCost);
        var withCost = await ReadAsync(costEditor.GetAsync(previewUrl));
        Assert.Equal(Fingerprint(preview), Fingerprint(withCost));
        var cost = withCost.GetProperty("changed").EnumerateArray().Single(c => Name(c.GetProperty("before")) == "А")
            .GetProperty("after").GetProperty("cost");
        Assert.Equal(300m, cost.GetProperty("total").GetDecimal());
        Assert.Equal(30m, cost.GetProperty("margin").GetDecimal());
        Assert.Equal(Version(offer), Version(await ReadAsync(scene.Manager.GetAsync(url))));

        var applied = await ReadAsync(SendAsync(scene.Manager, HttpMethod.Post, $"{url}/rebase", Version(offer),
            new { revisionId = target, fingerprint = Fingerprint(preview) }));
        Assert.Equal(["Снятая", "Дубль А", "Дубль Б"], applied.GetProperty("removed").EnumerateArray().Select(Name));
        var updated = applied.GetProperty("offer");
        Assert.Equal(Url(offer), Url(updated));
        Assert.NotEqual(Version(offer), Version(updated));
        Assert.Equal(1, updated.GetProperty("currentVersion").GetInt32());
        Assert.Equal("draft", updated.GetProperty("status").GetString());
        Assert.Equal("current-changed", updated.GetProperty("drift").GetProperty("code").GetString());
        var after = updated.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(["Б", "В", "А", "Добавленная", "Спорная"], after.Select(Name));
        Assert.Equal([460m, 600m, 165m, 960m, 240m], after.Select(Price));
        Assert.Equal(10m, after[2].GetProperty("ownMarkup").GetDecimal());
        Assert.Equal(JsonValueKind.Null, after[4].GetProperty("ownMarkup").ValueKind);
        Assert.Empty((await ReadAsync(scene.Manager.GetAsync($"{url}/versions"))).EnumerateArray());

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
        Assert.True(await db.EstimateRevisions.Where(r => r.Id == target).Select(r => r.FrozenAt != null).SingleAsync());
        Assert.Equal(first, await EstimateSeed.CurrentRevisionAsync(host, scene.EstimateId));
    }

    [Fact]
    public async Task Версия_проверяется_до_отпечатка_а_новая_редакция_обновляет_предпросмотр()
    {
        var scene = await NewAsync(host);
        var first = await scene.RevisionAsync(Row("Кабель"));
        var offer = await scene.CreateAsync(first);
        var target = await scene.RevisionAsync(Row("Кабель", cost: 200m));
        await scene.MakeCurrentAsync(target);
        var url = Url(offer);
        var preview = await ReadAsync(scene.Manager.GetAsync($"{url}/rebase-preview"));
        Assert.Equal(target, preview.GetProperty("target").GetProperty("id").GetGuid());

        await ReadAsync(SendAsync(scene.Manager, HttpMethod.Post, $"{url}/rebase", null,
            new { revisionId = target, fingerprint = Fingerprint(preview) }), HttpStatusCode.BadRequest);
        var stale = await ReadAsync(SendAsync(scene.Manager, HttpMethod.Post, $"{url}/rebase", "0",
            new { revisionId = Guid.NewGuid(), fingerprint = "wrong" }), HttpStatusCode.Conflict);
        Assert.False(stale.TryGetProperty("preview", out _));
        await ReadAsync(SendAsync(scene.Manager, HttpMethod.Post, $"{url}/rebase", Version(offer), new { }),
            HttpStatusCode.BadRequest);
        foreach (var permission in new[] { PlanModule.OfferRead, PlanModule.OfferEdit })
        {
            var restricted = await GrantedSignIn.WithPermissionsAsync(host, permission);
            using var read = await restricted.GetAsync($"{url}/rebase-preview");
            using var write = await SendAsync(restricted, HttpMethod.Post, $"{url}/rebase", Version(offer),
                new { fingerprint = Fingerprint(preview) });
            foreach (var response in new[] { read, write })
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                var content = await response.Content.ReadAsStringAsync();
                Assert.DoesNotContain("\"price\"", content);
                Assert.DoesNotContain("\"preview\"", content);
            }
        }

        var unrelated = await EstimateSeed.EstimateAsync(host, scene.Site.Construction);
        var foreign = (await EstimateSeed.RevisionAsync(host, unrelated, Row("Чужая строка"))).Id;
        await ReadAsync(scene.Manager.GetAsync($"{url}/rebase-preview?revisionId={foreign}"), HttpStatusCode.BadRequest);
        var wrong = await ReadAsync(SendAsync(scene.Manager, HttpMethod.Post, $"{url}/rebase", Version(offer),
            new { fingerprint = "wrong" }), HttpStatusCode.Conflict);
        Assert.Equal(Fingerprint(preview), Fingerprint(wrong.GetProperty("preview")));

        await scene.RevisionAsync(Row("Кабель", cost: 300m));
        var moved = await ReadAsync(SendAsync(scene.Manager, HttpMethod.Post, $"{url}/rebase", Version(offer),
            new { revisionId = target, fingerprint = Fingerprint(preview) }), HttpStatusCode.Conflict);
        var fresh = moved.GetProperty("preview");
        Assert.NotEqual(Fingerprint(preview), Fingerprint(fresh));
        Assert.Equal(target, fresh.GetProperty("target").GetProperty("id").GetGuid());
        Assert.Equal(240m, Price(Assert.Single(fresh.GetProperty("changed").EnumerateArray()).GetProperty("after")));
        Assert.Equal(Version(offer), Version(await ReadAsync(scene.Manager.GetAsync(url))));
    }

    [Fact]
    public async Task После_удаления_цели_подтверждение_возвращает_предпросмотр_по_действующей()
    {
        var scene = await NewAsync(host);
        var first = await scene.RevisionAsync(Row("Кабель"));
        var offer = await scene.CreateAsync(first);
        var target = await scene.RevisionAsync(Row("Кабель", cost: 200m));
        var active = await scene.RevisionAsync(Row("Кабель", cost: 500m));
        await scene.MakeCurrentAsync(active);
        var url = Url(offer);
        var preview = await ReadAsync(scene.Manager.GetAsync($"{url}/rebase-preview?revisionId={target}"));
        var version = await RateMatchFlow.VersionAsync(scene.Manager, scene.EstimateId, target);
        using var deleted = await scene.Manager.SendAsync(RateMatchFlow.With(HttpMethod.Delete, $"/api/plan/revisions/{target}", version));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var refused = await ReadAsync(SendAsync(scene.Manager, HttpMethod.Post, $"{url}/rebase", Version(offer),
            new { revisionId = target, fingerprint = Fingerprint(preview) }), HttpStatusCode.Conflict);
        var fresh = refused.GetProperty("preview");
        Assert.Equal(active, fresh.GetProperty("target").GetProperty("id").GetGuid());
        Assert.Equal(600m, Price(Assert.Single(fresh.GetProperty("changed").EnumerateArray()).GetProperty("after")));
        Assert.Equal(Version(offer), Version(await ReadAsync(scene.Manager.GetAsync(url))));
    }

    [Fact]
    public async Task Новое_сопоставление_цели_отзывает_предпросмотр_с_тем_же_номером_редакции()
    {
        var scene = await NewAsync(host);
        var first = await scene.RevisionAsync(Row("Кабель"));
        var target = await scene.RevisionAsync(Row("Кабель"));
        var offer = await scene.CreateAsync(first);
        var url = Url(offer);
        var preview = await ReadAsync(scene.Manager.GetAsync($"{url}/rebase-preview?revisionId={target}"));
        Assert.Empty(preview.GetProperty("added").EnumerateArray());

        var row = Assert.Single(await RateMatchFlow.RowsAsync(scene.Manager, target)).GetProperty("id").GetGuid();
        var classifier = await EstimateSeed.ClassifierAsync(host);
        var targetVersion = await RateMatchFlow.VersionAsync(scene.Manager, scene.EstimateId, target);
        var editor = await EstimateImportFlow.EditorAsync(host);
        await ReadAsync(editor.SendAsync(RateMatchFlow.MatchRow(target, row, targetVersion, new { workTypeId = classifier.WorkType })));

        var refused = await ReadAsync(SendAsync(scene.Manager, HttpMethod.Post, $"{url}/rebase", Version(offer),
            new { revisionId = target, fingerprint = Fingerprint(preview) }), HttpStatusCode.Conflict);
        var fresh = refused.GetProperty("preview");
        Assert.NotEqual(Fingerprint(preview), Fingerprint(fresh));
        Assert.Equal(row, Assert.Single(fresh.GetProperty("added").EnumerateArray()).GetProperty("revisionRowId").GetGuid());
        Assert.Single(fresh.GetProperty("removed").EnumerateArray());
        Assert.Equal(Version(offer), Version(await ReadAsync(scene.Manager.GetAsync(url))));
    }

    [Theory]
    [InlineData("sent")]
    [InlineData("accepted")]
    [InlineData("rejected")]
    [InlineData("no-answer")]
    public async Task Отправленное_КП_однократно_переходит_в_новый_черновик_и_сохраняет_снимок(string status)
    {
        var scene = await NewAsync(host);
        var first = await scene.RevisionAsync(Row("До переноса"));
        var target = await scene.RevisionAsync(Row("После переноса", cost: 600m) with { SourceUnit = "шт" });
        var offer = await scene.CreateAsync(first);
        var url = Url(offer);
        var sent = await ReadAsync(SendAsync(scene.Manager, HttpMethod.Post, $"{url}/send", Version(offer),
            new { sentOn = "2026-11-23" }));
        if (status != "sent")
            sent = await ReadAsync(SendAsync(scene.Manager, HttpMethod.Post, $"{url}/outcome", Version(sent), new { status }));
        var preview = await ReadAsync(scene.Manager.GetAsync($"{url}/rebase-preview?revisionId={target}"));
        var body = new { revisionId = target, fingerprint = Fingerprint(preview) };

        var results = await Task.WhenAll(
            SendAsync(scene.Manager, HttpMethod.Post, $"{url}/rebase", Version(sent), body),
            SendAsync(scene.Manager, HttpMethod.Post, $"{url}/rebase", Version(sent), body));
        var success = Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        var updated = (await ReadAsync(Task.FromResult(success))).GetProperty("offer");
        Assert.Equal(2, updated.GetProperty("currentVersion").GetInt32());
        Assert.Equal("draft", updated.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, updated.GetProperty("sentOn").ValueKind);
        Assert.Equal(target, updated.GetProperty("revision").GetProperty("id").GetGuid());
        Assert.Equal(720m, Price(updated.GetProperty("lines")[0]));

        var history = Assert.Single((await ReadAsync(scene.Manager.GetAsync($"{url}/versions"))).EnumerateArray());
        Assert.Equal(1, history.GetProperty("number").GetInt32());
        Assert.Equal(status, history.GetProperty("status").GetString());
        Assert.Equal("2026-11-23", history.GetProperty("sentOn").GetString());
        Assert.Equal(120m, history.GetProperty("total").GetDecimal());
        var snapshot = history.GetProperty("content");
        Assert.Equal(first, snapshot.GetProperty("revisionId").GetGuid());
        Assert.Equal("До переноса", Name(snapshot.GetProperty("lines")[0]));
        Assert.Equal("100 м", snapshot.GetProperty("lines")[0].GetProperty("unit").GetString());
        foreach (var result in results) result.Dispose();
    }

    private static string Name(JsonElement line) => line.GetProperty("name").GetString()!;

    private static decimal Price(JsonElement line) => line.GetProperty("price").GetDecimal();
}

[Collection(CostsOnlyCollection.Name)]
public class OfferRebaseDisabledModuleTests(CostsOnlyHost host)
{
    [Fact]
    public async Task Перенос_при_выключенном_модуле_возвращает_отказ_модуля()
    {
        var client = host.CreateClient();
        var url = $"/api/plan/offers/{Guid.NewGuid()}";
        Assert.Equal(HttpStatusCode.NotImplemented, (await client.GetAsync($"{url}/rebase-preview")).StatusCode);
        var refused = await ReadAsync(SendAsync(client, HttpMethod.Post, $"{url}/rebase", "1", new { fingerprint = "preview" }),
            HttpStatusCode.NotImplemented);
        Assert.Equal("plan", refused.GetProperty("module").GetString());
    }
}
