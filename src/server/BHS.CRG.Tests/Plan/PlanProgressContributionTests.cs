using System.Net;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using static BHS.CRG.Tests.Plan.OfferDerivativeScenario;

namespace BHS.CRG.Tests.Plan;

[Collection(PlanOnlyCollection.Name)]
public class PlanProgressContributionTests(PlanOnlyHost host)
{
    [Fact]
    public async Task План_берёт_действующие_редакции_всех_смет_и_показывает_17_несопоставленных()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        await EstimateSeed.RevisionAsync(host, estimate, await EstimateSeed.MatchedRowAsync(host, site, 100m));
        await EstimateSeed.RevisionAsync(host, estimate, await EstimateSeed.MatchedRowAsync(host, site, 200m));
        var current = await EstimateSeed.RevisionAsync(host, estimate,
            [await EstimateSeed.MatchedRowAsync(host, site, 300m), .. Enumerable.Range(1, 17)
                .Select(i => new EstimateRowDraft($"Без вида работы {i}", SourceVolume: 1m))]);
        await EstimateSeed.RevisionAsync(host, estimate, await EstimateSeed.MatchedRowAsync(host, site, 900m));
        using var editor = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead, PlanModule.EstimateEdit);
        var url = $"/api/plan/estimates/{estimate}";
        var view = await ReadAsync(editor.GetAsync(url));
        await ReadAsync(SendAsync(editor, HttpMethod.Post, $"{url}/current-revision", Version(view), new { revisionId = current.Id }));

        using var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead, CorePermissions.ConstructionsRead);
        var reply = await ProgressAsync(reader, site.Construction);
        var basis = Strip(reply, $"estimate-basis-{estimate}");
        Assert.Equal("План по: смета «Основная» ред. 3", basis.GetProperty("text").GetString());
        var link = $"/document-sets/{site.Construction}/estimates/{estimate}?revision={current.Id}";
        Assert.Equal(link, basis.GetProperty("link").GetString());
        var unmapped = Strip(reply, "estimate-unmapped");
        Assert.Equal("Строк сметы не сопоставлено: 17 — в таблице их нет", unmapped.GetProperty("text").GetString());
        Assert.Equal($"{link}&queue=unmatched", unmapped.GetProperty("link").GetString());

        var extra = await EstimateSeed.EstimateAsync(host, site.Construction, "Дополнительная", EstimateKind.Additional);
        await EstimateSeed.RevisionAsync(host, extra, await EstimateSeed.MatchedRowAsync(host, site, 50m));
        reply = await ProgressAsync(reader, site.Construction);
        Assert.Equal(350m, Cell(reply, "plan:estimate").GetProperty("volume").GetDecimal());
        Assert.Equal("План по: смета «Дополнительная» ред. 1", Strip(reply, $"estimate-basis-{extra}").GetProperty("text").GetString());
        Assert.Equal($"{link}&queue=unmatched", Strip(reply, "estimate-unmapped").GetProperty("link").GetString());
        AssertColumn(reply, "plan:estimate", "ok");
        AssertColumn(reply, "plan:schedule", "hidden", PlanModule.ScheduleRead);
        Assert.DoesNotContain("\"unitCost\"", reply.GetRawText());
        Assert.DoesNotContain("\"total\"", reply.GetRawText());
    }

    [Fact]
    public async Task Несопоставленные_на_разделе_ведут_ко_всем_действующим_редакциям_из_счётчика()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var main = await EstimateSeed.EstimateAsync(host, site.Construction);
        var first = await EstimateSeed.RevisionAsync(host, main, new EstimateRowDraft("Строка А", SourceVolume: 1));
        await EstimateSeed.RevisionAsync(host, main, await EstimateSeed.MatchedRowAsync(host, site, 50));

        var otherSection = await DistributionFlow.SectionAsync(host, site.Construction, "ЭОМ-2");
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
        var other = Estimate.Create(site.Construction, otherSection, "Смета второго корпуса", EstimateKind.Main, DateTimeOffset.UtcNow);
        db.Estimates.Add(other);
        await db.SaveChangesAsync();
        var second = await EstimateSeed.RevisionAsync(host, other.Id,
            new EstimateRowDraft("Строка Б", SourceVolume: 1), new EstimateRowDraft("Строка В", SourceVolume: 1));
        await EstimateSeed.RevisionAsync(host, other.Id, await EstimateSeed.MatchedRowAsync(host, site with { Section = otherSection }, 70));

        using var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead, CorePermissions.ConstructionsRead);
        foreach (var section in new Guid?[] { null, site.Section })
        {
            var response = await ProgressAsync(reader, site.Construction, section);
            var warning = Strip(response, "estimate-unmapped");
            Assert.Equal("Строк сметы не сопоставлено: 3 — в таблице их нет", warning.GetProperty("text").GetString());
            Assert.Equal(JsonValueKind.Null, warning.GetProperty("link").ValueKind);
            AssertLink(first.Id, main, "Смета «Основная» ред. 1: 1");
            AssertLink(second.Id, other.Id, "Смета «Смета второго корпуса» ред. 1: 2");

            void AssertLink(Guid revision, Guid estimate, string title)
            {
                var strip = Strip(response, $"estimate-unmapped-{revision}");
                Assert.Equal(title, strip.GetProperty("text").GetString());
                Assert.Equal($"/document-sets/{site.Construction}/estimates/{estimate}?revision={revision}&queue=unmatched",
                    strip.GetProperty("link").GetString());
            }
        }
    }

    [Fact]
    public async Task Отсутствие_числового_плана_отличается_от_явного_нуля()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var main = await EstimateSeed.EstimateAsync(host, site.Construction);
        var row = await EstimateSeed.MatchedRowAsync(host, site, 1m);
        await EstimateSeed.RevisionAsync(host, main, row with { Volume = null, SourceVolume = null });
        using var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead);
        var missing = await ProgressAsync(reader, site.Construction);
        Assert.Equal(JsonValueKind.Null, Cell(missing, "plan:estimate").GetProperty("volume").ValueKind);

        var extra = await EstimateSeed.EstimateAsync(host, site.Construction, "Дополнительная", EstimateKind.Additional);
        await EstimateSeed.RevisionAsync(host, extra, row with { Volume = 0m, SourceVolume = 0m });
        var zero = await ProgressAsync(reader, site.Construction);
        Assert.Equal(0m, Cell(zero, "plan:estimate").GetProperty("volume").GetDecimal());
    }

    [Fact]
    public async Task Отбор_по_разделу_берёт_долю_а_нераспределённый_объём_называет_полосой()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var revision = await EstimateSeed.RevisionAsync(host, estimate,
            await EstimateSeed.MatchedRowAsync(host, site, 200m), await EstimateSeed.MatchedRowAsync(host, site, 400m));
        var rows = await DistributionFlow.RowIdsAsync(host, revision.Id);
        using var editor = await DistributionFlow.EditorAsync(host);
        await DistributionFlow.LinesAsync(editor, revision.Id, DistributionFlow.Line(rows[0], "row", (site.Section, 1.5m)));

        using var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead);
        var whole = await ProgressAsync(reader, site.Construction);
        Assert.Equal(600m, Cell(whole, "plan:estimate").GetProperty("volume").GetDecimal());
        Assert.DoesNotContain(whole.GetProperty("strips").EnumerateArray(), IsUndistributed);

        var onSection = await ProgressAsync(reader, site.Construction, site.Section);
        Assert.Equal(150m, Cell(onSection, "plan:estimate").GetProperty("volume").GetDecimal());
        var undistributed = Assert.Single(onSection.GetProperty("strips").EnumerateArray(), IsUndistributed);
        Assert.Equal("Не распределено по разделам: 400 м по одной работе — план всей стройки", undistributed.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Сроки_берутся_из_всех_графиков_и_не_открывают_смету_или_другой_раздел()
    {
        var scene = await ScheduleScenario.CreateAsync(host);
        using var editor = scene.Editor;
        var site = scene.Site;
        await scene.RevisionAsync(await EstimateSeed.MatchedRowAsync(host, site, 400m));
        var first = await scene.BuildAsync(name: "Первый график");
        var second = await scene.BuildAsync(name: "Второй график");
        await scene.ManualAsync(first, "2026-12-03", "2026-12-07", site.Section);
        await scene.ManualAsync(second, "2026-12-01", "2026-12-04", site.Section);
        await scene.ManualAsync(first, "2026-11-01", "2026-12-20");

        using var scheduleReader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.ScheduleRead);
        var whole = await ProgressAsync(scheduleReader, site.Construction);
        AssertDates(whole, "2026-11-01", "2026-12-20");
        var onSection = await ProgressAsync(scheduleReader, site.Construction, site.Section);
        AssertDates(onSection, "2026-12-01", "2026-12-07");
        AssertColumn(onSection, "plan:schedule", "ok");
        AssertColumn(onSection, "plan:estimate", "hidden", PlanModule.EstimateRead);
        Assert.Equal("hidden", Strip(onSection, "estimate").GetProperty("state").GetString());
        Assert.False(Assert.Single(onSection.GetProperty("rows").EnumerateArray()).GetProperty("cells").TryGetProperty("plan:estimate", out _));
        Assert.DoesNotContain("Основная", onSection.GetRawText());
        Assert.DoesNotContain(onSection.GetProperty("strips").EnumerateArray(), IsUndistributed);

        using var costOnly = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateCost);
        using var refused = await costOnly.GetAsync($"/api/constructions/{site.Construction}/progress");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    private static Task<JsonElement> ProgressAsync(HttpClient client, Guid site, Guid? section = null) =>
        ReadAsync(client.GetAsync($"/api/constructions/{site}/progress" + (section is { } id ? $"?section={id}" : "")));

    private static JsonElement Strip(JsonElement response, string code) =>
        Assert.Single(response.GetProperty("strips").EnumerateArray(), item => item.GetProperty("code").GetString() == code);

    private static JsonElement Cell(JsonElement response, string code) =>
        Assert.Single(response.GetProperty("rows").EnumerateArray()).GetProperty("cells").GetProperty(code);

    private static bool IsUndistributed(JsonElement strip) =>
        strip.GetProperty("code").GetString()!.StartsWith("estimate-undistributed", StringComparison.Ordinal);

    private static void AssertColumn(JsonElement response, string code, string state, string? permission = null)
    {
        var column = Assert.Single(response.GetProperty("columns").EnumerateArray(), item => item.GetProperty("code").GetString() == code);
        Assert.Equal(state, column.GetProperty("state").GetString());
        Assert.Equal(permission, column.GetProperty("permission").GetString());
    }

    private static void AssertDates(JsonElement response, string start, string end)
    {
        var cell = Cell(response, "plan:schedule");
        Assert.Equal(start, cell.GetProperty("start").GetString());
        Assert.Equal(end, cell.GetProperty("end").GetString());
    }
}
