using System.Net;
using System.Net.Http.Json;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static BHS.CRG.Tests.Plan.OfferDerivativeScenario;
using static BHS.CRG.Tests.Plan.ScheduleScenario;

namespace BHS.CRG.Tests.Plan;

[Collection(PlanOnlyCollection.Name)]
public class ScheduleBuildTests(PlanOnlyHost host)
{
    [Fact]
    public async Task Части_повторяющиеся_строки_и_две_одновременные_попытки_построения()
    {
        var scene = await CreateAsync(host);
        var other = await DistributionFlow.SectionAsync(host, scene.Site.Construction, "ЭОМ-2");
        var cable = await EstimateSeed.MatchedRowAsync(host, scene.Site, 1400m);
        var revision = await scene.RevisionAsync([
            cable with { LsrSectionCode = "1", LsrSectionName = "Монтаж", NumberInSection = "1" },
            cable with { LsrSectionCode = "1", LsrSectionName = "Монтаж", NumberInSection = "2" },
            cable with { LsrSectionCode = "2", LsrSectionName = "Монтаж", NumberInSection = "1" },
            .. Enumerable.Range(1, 17).Select(i => Row($"Без вида {i}", "3", i.ToString())),
            Row("Материал") with { Kind = EstimateRowKind.Material }]);
        var rows = await DistributionFlow.RowIdsAsync(host, revision);
        await DistributionFlow.RuleAsync(scene.Editor, revision, "1", DistributionFlow.Section(scene.Site.Section));
        await DistributionFlow.LinesAsync(scene.Editor, revision,
            DistributionFlow.Line(rows[0], "plan", (scene.Site.Section, 700m), (other, 500m)));
        var request = scene.Creation(revision);

        var replies = await Task.WhenAll(scene.Editor.PostAsJsonAsync(Root, request), scene.Editor.PostAsJsonAsync(Root, request));
        var built = await ReadAsync(Task.FromResult(Assert.Single(replies, r => r.StatusCode == HttpStatusCode.Created)),
            HttpStatusCode.Created);
        var repeat = await ReadAsync(Task.FromResult(Assert.Single(replies, r => r.StatusCode == HttpStatusCode.OK)));
        Assert.Equal(Id(built), Id(repeat));
        Assert.Equal(17, Skipped(built));
        Assert.Equal(5, Works(built).Length);
        var split = Works(built).Where(w => GuidOrNull(w, "sourceRowId") == rows[0]).ToList();
        Assert.Equal(700m, split.Single(w => GuidOrNull(w, "sectionId") == scene.Site.Section).GetProperty("volume").GetDecimal());
        Assert.Equal(500m, split.Single(w => GuidOrNull(w, "sectionId") == other).GetProperty("volume").GetDecimal());
        Assert.Equal(200m, split.Single(w => GuidOrNull(w, "sectionId") is null).GetProperty("volume").GetDecimal());
        Assert.Equal(scene.Site.Section, GuidOrNull(Assert.Single(Works(built), w => GuidOrNull(w, "sourceRowId") == rows[1]), "sectionId"));
        Assert.Null(GuidOrNull(Assert.Single(Works(built), w => GuidOrNull(w, "sourceRowId") == rows[2]), "sectionId"));
        Assert.Equal(3, Works(built).Select(w => GuidOrNull(w, "planItemId")).Distinct().Count());

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
        var catalog = await ReadAsync(scene.Editor.GetAsync($"{Root}?constructionId={scene.Site.Construction}"));
        Assert.Equal(Id(built), Id(Assert.Single(catalog.EnumerateArray())));
        var frozen = await db.EstimateRevisions.SingleAsync(r => r.Id == revision);
        Assert.Equal("schedule", frozen.FrozenCode);
        var matchVersion = await RateMatchFlow.VersionAsync(scene.Editor, scene.Estimate, revision);
        using var refused = await scene.Editor.SendAsync(RateMatchFlow.MatchRow(revision, rows[0], matchVersion, new { clear = true }));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Электромонтаж", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Перестроение_сверяет_версию_и_отпечаток_и_сохраняет_совпавшую_работу()
    {
        var scene = await CreateAsync(host);
        var other = await DistributionFlow.SectionAsync(host, scene.Site.Construction, "ЭОМ-2");
        var third = await DistributionFlow.SectionAsync(host, scene.Site.Construction, "ЭОМ-3");
        var row = await EstimateSeed.MatchedRowAsync(host, scene.Site, 1400m);
        var revision = await scene.RevisionAsync(row, Row("Будущее сопоставление"));
        var rows = await DistributionFlow.RowIdsAsync(host, revision);
        await DistributionFlow.LinesAsync(scene.Editor, revision,
            DistributionFlow.Line(rows[0], "plan", (scene.Site.Section, 700m), (other, 500m)));
        var schedule = await scene.BuildAsync(revision);
        var url = ScheduleUrl(schedule);
        var work = Assert.Single(Works(schedule), w => GuidOrNull(w, "sectionId") == scene.Site.Section);
        var employeeId = await ScheduleWorkTests.EmployeeAsync(host);
        await ReadAsync(SendAsync(scene.Editor, HttpMethod.Put, WorkUrl(schedule, work), Version(work),
            new { volume = 700m, start = "2026-12-01", end = "2026-12-04", employeeIds = new[] { employeeId } }));
        var preview = await ReadAsync(scene.Editor.GetAsync($"{url}/rebuild-preview"));
        var savedVersion = Version(preview);
        await DistributionFlow.LinesAsync(scene.Editor, revision,
            DistributionFlow.Line(rows[0], "plan", (scene.Site.Section, 600m), (third, 600m)));
        var matchVersion = await RateMatchFlow.VersionAsync(scene.Editor, scene.Estimate, revision);
        await ReadAsync(scene.Editor.SendAsync(RateMatchFlow.MatchRow(revision, rows[1], matchVersion,
            new { workTypeId = row.WorkTypeId, factor = 100m })));
        var changed = await ReadAsync(scene.Editor.GetAsync(url));
        Assert.Equal(["distribution-changed"], RateMatchFlow.Marks(changed));
        Assert.Equal(3, Works(changed).Length);
        var body = new { fingerprint = Fingerprint(preview) };
        await ReadAsync(SendAsync(scene.Editor, HttpMethod.Post, $"{url}/rebuild", null, body), HttpStatusCode.BadRequest);
        await ReadAsync(SendAsync(scene.Editor, HttpMethod.Post, $"{url}/rebuild", Version(schedule), body), HttpStatusCode.Conflict);
        var conflict = await ReadAsync(SendAsync(scene.Editor, HttpMethod.Post, $"{url}/rebuild", savedVersion, body),
            HttpStatusCode.Conflict);
        var fresh = conflict.GetProperty("preview");
        Assert.Equal(2, fresh.GetProperty("added").GetArrayLength());
        Assert.Single(fresh.GetProperty("removed").EnumerateArray());
        Assert.Single(fresh.GetProperty("changed").EnumerateArray());
        var rebuilt = await ReadAsync(SendAsync(scene.Editor, HttpMethod.Post, $"{url}/rebuild", Version(fresh),
            new { fingerprint = Fingerprint(fresh) }));
        Assert.Empty(RateMatchFlow.Marks(rebuilt));
        Assert.Equal(0, Skipped(rebuilt));
        Assert.Equal(4, Works(rebuilt).Length);
        var kept = Assert.Single(Works(rebuilt), w => Id(w) == Id(work));
        Assert.Equal(600m, kept.GetProperty("volume").GetDecimal());
        Assert.Equal("2026-12-01", kept.GetProperty("start").GetString());
        Assert.Equal("2026-12-04", kept.GetProperty("end").GetString());
        Assert.Equal(employeeId, Assert.Single(kept.GetProperty("employeeIds").EnumerateArray()).GetGuid());
        var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.ScheduleRead);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"{url}/rebuild-preview")).StatusCode);
    }
}
