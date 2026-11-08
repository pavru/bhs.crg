using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Distribution;
using BHS.CRG.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static BHS.CRG.Tests.Plan.DistributionFlow;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Перенос распределения с базой (change <c>plan-10-distribution</c>, задачи 1.5 и 1.8, ТЗ PLAN-5.1, COST-13): выбор
/// источника, отчёт у новой редакции и решения по нему, числа переноса в ответе создания редакции.
/// </summary>
[Collection(PlanOnlyCollection.Name)]
public class DistributionCarryOverStoreTests(PlanOnlyHost host)
{
    private const string Lsr = "Кабельные линии";

    /// <summary>Новая редакция сметы — как её создаёт импорт: запись строк и перенос одной фиксацией.</summary>
    private async Task<(Guid Revision, CarryOverSummary? Summary)> ReimportAsync(Guid estimateId, params EstimateRowDraft[] rows)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
        var estimate = await db.Estimates.SingleAsync(e => e.Id == estimateId);
        var revision = await scope.ServiceProvider.GetRequiredService<EstimateRevisionWriter>()
            .StageAsync(estimate, new EstimateRevisionSource(FileName: "смета.gsfx"), rows);
        var summary = await scope.ServiceProvider.GetRequiredService<DistributionCarryOver>().StageAsync(estimate, revision, default);
        await db.SaveChangesAsync();
        return (revision.Id, summary);
    }

    [Fact]
    public async Task Источник_редакция_с_последним_изменением_распределения_не_обязательно_действующая()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        EstimateRowDraft[] rows = [Row("1", Lsr, "ГЭСН-1", 2m, "1")];
        var current = (await EstimateSeed.RevisionAsync(host, estimate, rows)).Id;
        var other = (await EstimateSeed.RevisionAsync(host, estimate, rows)).Id;
        Assert.Equal(current, await EstimateSeed.CurrentRevisionAsync(host, estimate));
        var editor = await EditorAsync(host);
        await RuleAsync(editor, current, "1", Whole);
        await RuleAsync(editor, other, "1", Section(site.Section));

        var (next, summary) = await ReimportAsync(estimate, rows);

        Assert.Equal(2, summary!.SourceNumber);
        var view = await GetAsync(editor, next);
        Assert.Equal(site.Section, RuleOf(view, "1").GetProperty("target").GetProperty("sectionId").GetGuid());
        var counts = view.GetProperty("carryOver").GetProperty("counts");
        Assert.Equal(other, counts.GetProperty("sourceRevisionId").GetGuid());
        Assert.Equal(2, counts.GetProperty("sourceNumber").GetInt32());
    }

    [Fact]
    public async Task Отчёт_переноса_хранится_у_редакции_и_решается_в_строке()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var second = await SectionAsync(host, site.Construction, "ЭОМ-2");
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var first = (await EstimateSeed.RevisionAsync(host, estimate,
            Row("1", Lsr, "A", 12m, "1"), Row("1", Lsr, "X", 4m, "2"), Row("1", Lsr, "X", 4m, "3"),
            Row("1", Lsr, "Y", 12m, "4"), Row("1", Lsr, "Z", 2m, "5"))).Id;
        var old = await RowIdsAsync(host, first);
        var editor = await EditorAsync(host);
        await LinesAsync(editor, first,
            Line(old[0], "row", (site.Section, 7m), (second, 5m)), Line(old[1], "row", (site.Section, 4m)),
            Line(old[2], "row", (second, 4m)), Line(old[3], "row", (site.Section, 7m), (second, 5m)),
            Line(old[4], "row", (site.Section, 2m)));

        var (revision, summary) = await ReimportAsync(estimate,
            Row("1", Lsr, "A", 12m, "1"), Row("1", Lsr, "X", 4m, "2"), Row("1", Lsr, "X", 3m, "3"),
            Row("1", Lsr, "X", 4m, "4"), Row("1", Lsr, "Y", 10m, "5"));
        Assert.Equal((1, 0, 2, 1, 1), (summary!.Carried, summary.Shifted, summary.Ambiguous, summary.Overflow, summary.Lost));
        var rows = await RowIdsAsync(host, revision);
        Assert.Equal(4, (await CountsAsync(editor, estimate, revision)).GetProperty("carryOverPending").GetInt32());

        var view = await GetAsync(editor, revision);
        Assert.Equal(2, LineOf(view, rows[0]).GetProperty("parts").GetArrayLength());
        var items = view.GetProperty("carryOver").GetProperty("items").EnumerateArray().ToList();
        var ambiguous = items.Where(i => i.GetProperty("category").GetString() == "ambiguous").ToList();
        Assert.All(ambiguous, i => Assert.Equal(rows[1..4], i.GetProperty("candidates").EnumerateArray().Select(c => c.GetGuid())));
        var overflow = Assert.Single(items, i => i.GetProperty("category").GetString() == "overflow");
        Assert.Equal(rows[4], overflow.GetProperty("rowId").GetGuid());
        Assert.Equal(10m, overflow.GetProperty("newVolume").GetDecimal());
        var lost = Assert.Single(items, i => i.GetProperty("category").GetString() == "lost");
        Assert.Equal("Z", lost.GetProperty("rateCode").GetString());
        Assert.Equal(2m, Assert.Single(lost.GetProperty("parts").EnumerateArray()).GetProperty("quantity").GetProperty("row").GetDecimal());

        var (b1, b2) = (ambiguous.Single(i => i.GetProperty("number").GetString() == "2").GetProperty("id").GetGuid(),
            ambiguous.Single(i => i.GetProperty("number").GetString() == "3").GetProperty("id").GetGuid());
        async Task<HttpResponseMessage> DecideAsync(Guid item, object body) =>
            await editor.SendAsync(Decide(revision, item, await VersionAsync(editor, revision), body));

        // Не из кандидатов — 400; у кандидата уже есть части — 409, и они целы.
        Assert.Equal(HttpStatusCode.BadRequest, (await DecideAsync(b1, new { action = "apply", rowId = rows[0] })).StatusCode);
        await LinesAsync(editor, revision, Line(rows[3], "row", (second, 1m)));
        Assert.Equal(HttpStatusCode.Conflict, (await DecideAsync(b1, new { action = "apply", rowId = rows[3] })).StatusCode);
        Assert.Equal(1m, Assert.Single(LineOf(await GetAsync(editor, revision), rows[3]).GetProperty("parts").EnumerateArray())
            .GetProperty("quantity").GetProperty("row").GetDecimal());

        // На кандидата — прежние части на месте, позиция из отчёта ушла, журнал получил запись.
        var applied = await DecideAsync(b1, new { action = "apply", rowId = rows[1] });
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var after = (await applied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("distribution");
        Assert.Equal(site.Section, Assert.Single(LineOf(after, rows[1]).GetProperty("parts").EnumerateArray()).GetProperty("sectionId").GetGuid());
        Assert.DoesNotContain(after.GetProperty("carryOver").GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == b1);
        Assert.Equal(3, after.GetProperty("carryOver").GetProperty("pending").GetInt32());
        Assert.Single(await JournalAsync(host, revision, "plan.distribution.carry-over"));

        // Части не помещаются в кандидата — 400 со строкой; «не переносить» — решено.
        var tooBig = await DecideAsync(b2, new { action = "apply", rowId = rows[2] });
        Assert.Equal(HttpStatusCode.BadRequest, tooBig.StatusCode);
        Assert.Equal(rows[2], Assert.Single((await tooBig.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").EnumerateArray())
            .GetProperty("rowId").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(b2, new { action = "skip" })).StatusCode);

        // «Понятно» — у overflow и lost; apply им не годится.
        var lostId = lost.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await DecideAsync(lostId, new { action = "apply", rowId = rows[0] })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(lostId, new { action = "acknowledge" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(overflow.GetProperty("id").GetGuid(), new { action = "acknowledge" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await DecideAsync(lostId, new { action = "acknowledge" })).StatusCode);

        var done = await GetAsync(editor, revision);
        Assert.Equal(0, done.GetProperty("carryOver").GetProperty("pending").GetInt32());
        Assert.Empty(done.GetProperty("carryOver").GetProperty("items").EnumerateArray());
        Assert.Equal(0, (await CountsAsync(editor, estimate, revision)).GetProperty("carryOverPending").GetInt32());
    }

    /// <summary>Повторный импорт файлом: ответ создания редакции несёт источник и числа, правило перенесено.</summary>
    [Fact]
    public async Task Повторный_импорт_файлом_переносит_правило_и_называет_числа()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var editor = await EditorAsync(host);
        var file = RateMatchFlow.Sample(RateMatchFlow.UniqueCode());
        var (estimate, first) = await RateMatchFlow.ImportAsync(editor, site.Construction, file);
        var sections = (await GetAsync(editor, first)).GetProperty("rules").EnumerateArray()
            .Select(r => r.GetProperty("lsrSectionCode").GetString()).ToList();
        await RuleAsync(editor, first, sections[0], Section(site.Section));

        var report = await EstimateImportFlow.OutcomeAsync(editor, await EstimateImportFlow.UploadAsync(editor,
            EstimateImportFlow.Form(file, "Data.xml", site.Construction, estimateId: estimate)));
        var committed = await editor.SendAsync(
            EstimateImportFlow.Commit(EstimateImportFlow.Id(report), report.GetProperty("version").GetString()));
        Assert.True(committed.StatusCode == HttpStatusCode.Created, await committed.Content.ReadAsStringAsync());
        var done = await committed.Content.ReadFromJsonAsync<JsonElement>();
        var carry = done.GetProperty("distributionCarryOver");
        Assert.Equal(first, carry.GetProperty("sourceRevisionId").GetGuid());
        Assert.Equal(0, carry.GetProperty("lost").GetInt32());

        var next = done.GetProperty("revisionId").GetGuid();
        Assert.Equal(site.Section, RuleOf(await GetAsync(editor, next), sections[0]).GetProperty("target")
            .GetProperty("sectionId").GetGuid());
        Assert.Single(await JournalAsync(host, next, "plan.distribution.carry-over"));
    }
}
