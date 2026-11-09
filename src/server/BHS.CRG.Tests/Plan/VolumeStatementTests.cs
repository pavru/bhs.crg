using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Ведомость объёмов работ приложением к смете (change <c>plan-11-conjuncture</c>, решение В-7 (а), ТЗ PLAN-7): тем же
/// путём, что конъюнктурный анализ, и на план сметы не влияет.
/// </summary>
[Collection(PlanOnlyCollection.Name)]
public class VolumeStatementTests(PlanOnlyHost host)
{
    /// <summary>Образец ВОР принят: строки с объёмами и обоснованием видны, а редакции, строки и действующая — прежние.</summary>
    [Fact]
    public async Task Образец_ВОР_принят_а_план_стройки_не_изменился()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var revision = await EstimateSeed.RevisionAsync(host, estimate, await EstimateSeed.MatchedRowAsync(host, site, 120m));
        var client = await EstimateImportFlow.EditorAsync(host);
        var before = await PlanAsync(estimate);

        var report = await ConjunctureFlow.ParsedAsync(client, estimate, ConjunctureFlow.Vor);
        Assert.Equal("vor", report.GetProperty("kind").GetString());
        Assert.Contains("приложите её к смете", report.GetProperty("marks")[0].GetProperty("text").GetString());
        var accepted = await ConjunctureFlow.AcceptAsync(client, estimate, EstimateImportFlow.Id(report));
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var attachment = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("attachment");
        Assert.Equal("vor", attachment.GetProperty("kind").GetString());

        var list = await client.GetFromJsonAsync<JsonElement>($"/api/plan/estimates/{estimate}/volume-statements");
        Assert.Equal(ConjunctureFlow.Id(attachment), ConjunctureFlow.Id(Assert.Single(list.EnumerateArray())));
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>($"/api/plan/estimates/{estimate}/conjuncture")).GetArrayLength());

        var page = await client.GetFromJsonAsync<JsonElement>(
            $"/api/plan/volume-statements/{ConjunctureFlow.Id(attachment)}/lines?limit=1000");
        var lines = page.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(attachment.GetProperty("parsed").GetInt32(), lines.Count);
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.GetProperty("quantity").GetDecimal() > 0 && !string.IsNullOrWhiteSpace(l.GetProperty("units").GetString()));
        Assert.Contains(lines, l => !string.IsNullOrWhiteSpace(l.GetProperty("code").GetString())
                                    || !string.IsNullOrWhiteSpace(l.GetProperty("drawings").GetString()));

        // Сводка строки списка: «48 работ в 7 главах»; поставщиков у ведомости нет вовсе.
        var row = list[0];
        Assert.Equal(lines.Select(l => l.GetProperty("chapterIndex").GetInt32()).Distinct().Count(),
            row.GetProperty("chapters").GetInt32());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("suppliers").ValueKind);
        Assert.All(lines, l => Assert.Equal(JsonValueKind.Null, l.GetProperty("parentNumber").ValueKind));

        Assert.Equal(before, await PlanAsync(estimate));
        Assert.Equal(revision.Id, await EstimateSeed.CurrentRevisionAsync(host, estimate));

        var path = $"/api/plan/volume-statements/{ConjunctureFlow.Id(attachment)}";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(ConjunctureFlow.Delete(path, null))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.SendAsync(ConjunctureFlow.Delete(path, ConjunctureFlow.Version(attachment)))).StatusCode);
        Assert.Equal(before, await PlanAsync(estimate));
    }

    /// <summary>
    /// «Входит в № 12» (вариант 1 макета П11): у подчинённой работы — номер ближайшей не подчинённой выше в той же
    /// главе, у прочих — ничего. Образец 15.3.5 — 67 подчинённых. Подчинённая первой строкой страницы (вышестоящая —
    /// на прошлой) номер всё равно называет.
    /// </summary>
    [Fact]
    public async Task Подчинённая_работа_называет_вышестоящую()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        await EstimateSeed.RevisionAsync(host, estimate, await EstimateSeed.MatchedRowAsync(host, site, 120m));
        var client = await EstimateImportFlow.EditorAsync(host);
        var attachment = await ConjunctureFlow.AttachedAsync(client, estimate, "vor-15.3.5-attachment-pdf");
        var path = $"/api/plan/volume-statements/{ConjunctureFlow.Id(attachment)}/lines";

        var lines = (await client.GetFromJsonAsync<JsonElement>($"{path}?limit=1000")).GetProperty("lines").EnumerateArray().ToList();
        var expected = ExpectedParents(lines);
        Assert.True(expected.Count(p => p is not null) > 10, "В образце 15.3.5 подчинённых работ десятки.");
        for (var i = 0; i < lines.Count; i++)
            Assert.Equal(expected[i], lines[i].GetProperty("parentNumber").GetString());

        var boundary = Enumerable.Range(1, lines.Count - 1).First(i => expected[i] is not null);
        var single = await client.GetFromJsonAsync<JsonElement>($"{path}?offset={boundary}&limit=1");
        Assert.Equal(expected[boundary], single.GetProperty("lines")[0].GetProperty("parentNumber").GetString());
    }

    private static List<string?> ExpectedParents(List<JsonElement> lines)
    {
        var result = new List<string?>();
        JsonElement? master = null;
        foreach (var line in lines)
        {
            if (master is { } m && m.GetProperty("chapterIndex").GetInt32() != line.GetProperty("chapterIndex").GetInt32())
                master = null;
            if (!line.GetProperty("subordinate").GetBoolean())
            {
                master = line;
                result.Add(null);
            }
            else result.Add(master?.GetProperty("number").GetString());
        }
        return result;
    }

    /// <summary>План сметы, как он лежит: редакции, действующая, строки с объёмами и распределение.</summary>
    private async Task<string> PlanAsync(Guid estimate)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
        var revisions = await db.EstimateRevisions.AsNoTracking().Where(r => r.EstimateId == estimate)
            .OrderBy(r => r.Number).Select(r => r.Id).ToListAsync();
        var current = await db.Estimates.AsNoTracking().Where(e => e.Id == estimate).Select(e => e.CurrentRevisionId).SingleAsync();
        var rows = await db.EstimateRows.AsNoTracking().Where(r => revisions.Contains(r.RevisionId))
            .OrderBy(r => r.Id).Select(r => new { r.Id, r.Volume, r.PlanItemId }).ToListAsync();
        var parts = await db.DistributionParts.AsNoTracking().Where(p => revisions.Contains(p.RevisionId)).CountAsync();
        return JsonSerializer.Serialize(new { revisions, current, rows, parts });
    }
}
