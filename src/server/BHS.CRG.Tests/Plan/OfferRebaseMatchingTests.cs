using System.Text.Json;
using BHS.CRG.Tests.Integration;
using static BHS.CRG.Tests.Plan.OfferDerivativeScenario;

namespace BHS.CRG.Tests.Plan;

[Collection(PlanOnlyCollection.Name)]
public class OfferRebaseMatchingTests(PlanOnlyHost host)
{
    [Fact]
    public async Task Вставка_повторной_расценки_не_переносит_наценку_по_номеру()
    {
        var scene = await NewAsync(host);
        var first = await scene.RevisionAsync(
            Row("Прокладка", number: "1"), Row("Прокладка", number: "2"), Row("Прокладка в щите", number: "3"));
        var target = await scene.RevisionAsync(
            Row("Прокладка", number: "1", cost: 200m), Row("Прокладка", number: "2", cost: 300m),
            Row("Прокладка", number: "3", cost: 400m), Row("Прокладка в щите", number: "4", cost: 200m));
        var offer = await scene.CreateAsync(first);
        var url = Url(offer);
        var lines = offer.GetProperty("lines");
        offer = await ReadAsync(SendAsync(scene.Manager, HttpMethod.Put, $"{url}/markups", Version(offer), new
        {
            markup = 20m,
            lines = new[]
            {
                new { lineId = lines[0].GetProperty("id").GetGuid(), markup = 70m },
                new { lineId = lines[2].GetProperty("id").GetGuid(), markup = 30m },
            },
        }));

        var preview = await ReadAsync(scene.Manager.GetAsync($"{url}/rebase-preview?revisionId={target}"));
        var ambiguous = Assert.Single(preview.GetProperty("unmatched").EnumerateArray());
        Assert.Equal(2, ambiguous.GetProperty("before").GetArrayLength());
        Assert.Equal(70m, ambiguous.GetProperty("before")[0].GetProperty("ownMarkup").GetDecimal());
        Assert.Equal([240m, 360m, 480m], ambiguous.GetProperty("after").EnumerateArray().Select(Price));
        var paired = Assert.Single(preview.GetProperty("changed").EnumerateArray()).GetProperty("after");
        Assert.Equal("4", paired.GetProperty("number").GetString());
        Assert.Equal(260m, Price(paired));

        var result = await ReadAsync(SendAsync(scene.Manager, HttpMethod.Post, $"{url}/rebase", Version(offer),
            new { revisionId = target, fingerprint = Fingerprint(preview) }));
        Assert.Equal(2, result.GetProperty("removed").GetArrayLength());
        var saved = result.GetProperty("offer").GetProperty("lines");
        Assert.Equal([240m, 360m, 480m, 260m], saved.EnumerateArray().Select(Price));
        Assert.Equal(JsonValueKind.Null, saved[0].GetProperty("ownMarkup").ValueKind);
        Assert.Equal(30m, saved[3].GetProperty("ownMarkup").GetDecimal());
    }

    [Fact]
    public async Task Наценка_следует_за_главой_а_двусмысленные_главы_названы_в_предпросмотре()
    {
        var scene = await NewAsync(host);
        var installation = Row("Кабель") with { LsrSectionName = "Монтаж" };
        var auxiliary = Row("Оснастка", "2") with { LsrSectionName = "Вспомогательные" };
        var first = await scene.RevisionAsync(installation, auxiliary);
        var target = await scene.RevisionAsync(
            Row("Кабель") with { LsrSectionName = "Демонтаж" },
            installation with { LsrSectionCode = "2", Total = 200m },
            auxiliary with { LsrSectionCode = "3" },
            auxiliary with { LsrSectionCode = "4" });
        var offer = await scene.CreateAsync(first, markup: 5m);
        var url = Url(offer);
        offer = await ReadAsync(SendAsync(scene.Manager, HttpMethod.Put, $"{url}/markups", Version(offer), new
        {
            markup = 5m,
            sections = new[] { new { code = "1", markup = 15m }, new { code = "2", markup = 40m } },
        }));

        var preview = await ReadAsync(scene.Manager.GetAsync($"{url}/rebase-preview?revisionId={target}"));
        var added = Assert.Single(preview.GetProperty("added").EnumerateArray());
        Assert.Equal("Демонтаж", added.GetProperty("lsrSection").GetProperty("name").GetString());
        Assert.Equal(105m, Price(added));
        var changed = Assert.Single(preview.GetProperty("changed").EnumerateArray()).GetProperty("after");
        Assert.Equal("Монтаж", changed.GetProperty("lsrSection").GetProperty("name").GetString());
        Assert.Equal(230m, Price(changed));
        var unresolved = Assert.Single(preview.GetProperty("unmatchedSectionMarkups").EnumerateArray());
        Assert.Equal("Вспомогательные", unresolved.GetProperty("source").GetProperty("name").GetString());
        Assert.Equal(40m, unresolved.GetProperty("markup").GetDecimal());
        Assert.Equal(["3", "4"], unresolved.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("code").GetString()));

        var result = await ReadAsync(SendAsync(scene.Manager, HttpMethod.Post, $"{url}/rebase", Version(offer),
            new { revisionId = target, fingerprint = Fingerprint(preview) }));
        var saved = result.GetProperty("offer");
        Assert.Equal([105m, 230m, 105m, 105m], saved.GetProperty("lines").EnumerateArray().Select(Price));
        var marked = Assert.Single(saved.GetProperty("sections").EnumerateArray(),
            s => s.GetProperty("markup").ValueKind == JsonValueKind.Number);
        Assert.Equal("2", marked.GetProperty("code").GetString());
        Assert.Equal(15m, marked.GetProperty("markup").GetDecimal());
    }

    private static decimal Price(JsonElement line) => line.GetProperty("price").GetDecimal();
}
