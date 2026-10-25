using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Сопоставление строк редакции с видом работы (change <c>plan-08-matching</c>, задачи 1.2, 1.3, 1.5; ТЗ PLAN-6.1,
/// PLAN-4, PLAN-5, CORE-13): коэффициент пересчёта, позиция перечня, раздел ЛСР одним действием, зафиксированная
/// редакция.
/// </summary>
[Collection(PlanOnlyCollection.Name)]
public class RateMatchingTests(PlanOnlyHost host)
{
    /// <summary>
    /// «100 м → м» с коэффициентом 100: позиция перечня в «м», объём плана 1200 из 12, объём файла — как в ЛСР.
    /// До сопоставления строка помечена <c>unmatched</c> и в план стройки не входит (ТЗ PLAN-4).
    /// </summary>
    [Fact]
    public async Task Сто_метров_в_метры_дают_позицию_в_метрах_и_объём_1200_из_12()
    {
        var (site, estimate, revision) = await RevisionAsync(Row(RateMatchFlow.UniqueCode(), 12m, "100 м"));
        var c = await EstimateSeed.ClassifierAsync(host);
        var editor = await EstimateImportFlow.EditorAsync(host);
        var row = Assert.Single(await RateMatchFlow.RowsAsync(editor, revision));
        Assert.Equal(["unmatched"], RateMatchFlow.Marks(row));
        Assert.Empty(await PlanAsync(site));

        // Коэффициент не назван — его предлагают единицы: «100 м» → «м» = 100.
        var response = await editor.SendAsync(RateMatchFlow.MatchRow(revision, Id(row),
            await RateMatchFlow.VersionAsync(editor, estimate, revision), new { workTypeId = c.WorkType }));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var matched = body.GetProperty("row");
        Assert.Equal(1200m, matched.GetProperty("volume").GetDecimal());
        Assert.Equal(12m, matched.GetProperty("sourceVolume").GetDecimal());
        Assert.Equal(100m, matched.GetProperty("factor").GetDecimal());
        Assert.Equal(c.Unit, matched.GetProperty("unitId").GetGuid());
        Assert.Empty(RateMatchFlow.Marks(matched));
        // Ответ называет обе единицы: ошибка «в сто раз» видна сразу.
        Assert.Equal("100 м", body.GetProperty("units").GetProperty("sourceUnit").GetString());
        Assert.Equal("м", body.GetProperty("units").GetProperty("unit").GetString());

        using var scope = host.Services.CreateScope();
        var item = Assert.Single(await scope.ServiceProvider.GetRequiredService<IModulePlanItems>()
            .GetAsync([matched.GetProperty("planItemId").GetGuid()]));
        Assert.Equal(new PlanItemKey(c.WorkType, site.Construction, null, c.Unit), item.Key);
        Assert.Equal([1200m], await PlanAsync(site));
    }

    /// <summary>
    /// Раздел ЛСР одним действием: несопоставленные строки раздела в названной единице сопоставлены, строка другой
    /// единицы пропущена с <c>differentUnit</c>, сопоставленная — с <c>alreadyMatched</c>; строки другого раздела
    /// не тронуты.
    /// </summary>
    [Fact]
    public async Task Раздел_ЛСР_одним_действием_строка_другой_единицы_пропущена()
    {
        var c = await EstimateSeed.ClassifierAsync(host);
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var already = await EstimateSeed.MatchedRowAsync(host, site, 5m);
        var revision = (await EstimateSeed.RevisionAsync(host, estimate,
            Row(RateMatchFlow.UniqueCode(), 2m, "100 м", "1"),
            Row(RateMatchFlow.UniqueCode(), 3m, "100 м", "1"),
            Row(RateMatchFlow.UniqueCode(), 40m, "м", "1"),
            already with { LsrSectionCode = "1" },
            Row(RateMatchFlow.UniqueCode(), 7m, "100 м", "2"))).Id;
        var editor = await EstimateImportFlow.EditorAsync(host);
        var rows = await RateMatchFlow.RowsAsync(editor, revision);

        var response = await editor.SendAsync(RateMatchFlow.MatchSection(revision,
            await RateMatchFlow.VersionAsync(editor, estimate, revision),
            new { lsrSectionCode = "1", workTypeId = c.WorkType, sourceUnit = "100 м" }));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(2, body.GetProperty("matched").GetInt32());
        var skipped = body.GetProperty("skipped").EnumerateArray()
            .ToDictionary(s => s.GetProperty("order").GetInt32(), s => s.GetProperty("reason").GetString());
        Assert.Equal(new Dictionary<int, string?> { [3] = "differentUnit", [4] = "alreadyMatched" }, skipped);
        Assert.Equal(2, body.GetProperty("memory").GetProperty("remembered").GetInt32());

        var after = await RateMatchFlow.RowsAsync(editor, revision);
        Assert.Equal([200m, 300m], after.Take(2).Select(r => r.GetProperty("volume").GetDecimal()));
        Assert.Equal(["unmatched"], RateMatchFlow.Marks(after[2]));
        Assert.Equal(["unmatched"], RateMatchFlow.Marks(after[4]));
        Assert.Equal(rows[3].GetProperty("planItemId").GetGuid(), after[3].GetProperty("planItemId").GetGuid());
    }

    /// <summary>
    /// Зафиксированная редакция (решение В-6): несопоставленную строку сопоставить можно, пересопоставить
    /// сопоставленную — 409 с названием производной и путём «новая редакция»; снять сопоставление — тоже 409.
    /// </summary>
    [Fact]
    public async Task Зафиксированная_несопоставленную_можно_пересопоставить_нельзя()
    {
        var c = await EstimateSeed.ClassifierAsync(host);
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var revision = (await EstimateSeed.RevisionAsync(host, estimate,
            await EstimateSeed.MatchedRowAsync(host, site, 10m), Row(RateMatchFlow.UniqueCode(), 1m, "100 м"))).Id;
        await FreezeAsync(revision, new EstimateFreeze("schedule", "график от 12.11.2026"));
        var editor = await EstimateImportFlow.EditorAsync(host);
        var rows = await RateMatchFlow.RowsAsync(editor, revision);

        var free = await editor.SendAsync(RateMatchFlow.MatchRow(revision, Id(rows[1]),
            await RateMatchFlow.VersionAsync(editor, estimate, revision), new { workTypeId = c.WorkType }));
        Assert.Equal(HttpStatusCode.OK, free.StatusCode);

        var version = await RateMatchFlow.VersionAsync(editor, estimate, revision);
        foreach (var body in new object[] { new { workTypeId = c.WorkType, factor = 1m }, new { clear = true } })
        {
            var refused = await editor.SendAsync(RateMatchFlow.MatchRow(revision, Id(rows[0]), version, body));
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var text = await refused.Content.ReadAsStringAsync();
            Assert.Contains("график от 12.11.2026", text);
            Assert.Contains("новая редакция", text);
        }
        Assert.Equal(100m, RateMatchFlow.Stored(host, Id(rows[0])).Factor);
    }

    /// <summary>
    /// Снять сопоставление — тем же адресом с <c>clear</c>: строка снова «не сопоставлена» и из плана уходит. Пустое
    /// тело не снимает ничего — забытое поле не стирает сопоставление.
    /// </summary>
    [Fact]
    public async Task Снятие_сопоставления_только_явным_clear()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var revision = (await EstimateSeed.RevisionAsync(host, estimate, await EstimateSeed.MatchedRowAsync(host, site, 10m))).Id;
        var editor = await EstimateImportFlow.EditorAsync(host);
        var row = Assert.Single(await RateMatchFlow.RowsAsync(editor, revision));
        var version = await RateMatchFlow.VersionAsync(editor, estimate, revision);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await editor.SendAsync(RateMatchFlow.MatchRow(revision, Id(row), version, new { }))).StatusCode);

        var cleared = await editor.SendAsync(RateMatchFlow.MatchRow(revision, Id(row), version, new { clear = true }));
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var body = await cleared.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["unmatched"], RateMatchFlow.Marks(body.GetProperty("row")));
        Assert.Null(RateMatchFlow.Stored(host, Id(row)).PlanItemId);
        Assert.NotEqual(version, body.GetProperty("version").GetString());
    }

    internal static EstimateRowDraft Row(string code, decimal volume, string unit, string? section = null) =>
        new($"Строка {code}", LsrSectionCode: section, LsrSectionName: section is null ? null : $"Раздел {section}",
            RateCode: code, SourceVolume: volume, SourceUnit: unit);

    private async Task<(EstimateSeed.Site Site, Guid Estimate, Guid Revision)> RevisionAsync(params EstimateRowDraft[] rows)
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        return (site, estimate, (await EstimateSeed.RevisionAsync(host, estimate, rows)).Id);
    }

    /// <summary>План стройки — объёмы строк, которые в него входят (ТЗ PLAN-4).</summary>
    private async Task<List<decimal?>> PlanAsync(EstimateSeed.Site site)
    {
        using var scope = host.Services.CreateScope();
        return await PlanRows.Current(scope.ServiceProvider.GetRequiredService<PlanDbContext>(), site.Construction)
            .Select(r => r.Volume).ToListAsync();
    }

    private async Task FreezeAsync(Guid revisionId, EstimateFreeze reason)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
        (await db.EstimateRevisions.SingleAsync(r => r.Id == revisionId)).Freeze(reason, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
    }

    private static Guid Id(JsonElement row) => row.GetProperty("id").GetGuid();
}
