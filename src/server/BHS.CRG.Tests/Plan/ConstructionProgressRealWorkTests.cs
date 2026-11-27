using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;
using BHS.CRG.Tests.Work;

namespace BHS.CRG.Tests.Plan;

public sealed class ConstructionProgressWorkHost : AssignmentHost
{
    protected override string ModuleCode => "plan,work";
    protected override string DatabaseSuffix => "progress_real_work";
}

public class ConstructionProgressRealWorkTests(ConstructionProgressWorkHost host) : IClassFixture<ConstructionProgressWorkHost>, IAsyncLifetime
{
    private readonly CrewWorld crew = new(host);

    public Task InitializeAsync() => crew.SeedAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Labor_strip_respects_report_scope_norm_rights_and_the_selected_month()
    {
        var world = new WorkLaborWorld(host);
        // Тот же посев, что у факта; используем его справочники для сравнения Work.
        await world.SeedAsync();
        var site = world.Reports.SiteA;
        var section = await world.SectionAsync(site, "Корпус А");
        var person = await world.Reports.EmployeeAsync("Монтажник");
        await world.NormAsync(site, section, .5m);
        await world.ReportAsync(site, person, 8, ReportStatus.Accepted, WorkLaborWorld.Day,
            world.Line(12, section), world.Line(4, section, world.Reports.WorkTypeNeedsPlace));
        var (_, manager) = await world.Reports.ManagerAsync("Менеджер", site, PlanModule.LaborRead);
        var (_, hidden) = await world.Reports.ManagerAsync("Без норм", site);
        var (_, otherSite) = await world.Reports.ManagerAsync("Другая стройка", world.Reports.SiteB, PlanModule.EstimateRead);
        using var executive = await GrantedSignIn.WithPermissionsAsync(host, WorkModule.ReportRead, PlanModule.EstimateRead);
        using var factsOnly = await GrantedSignIn.WithPermissionsAsync(host, WorkModule.FactsRead, PlanModule.LaborRead);
        var path = $"/api/constructions/{site}/progress";
        var atMonth = path + "?laborMonth=2026-11-01";
        var shown = Strip(await manager.GetFromJsonAsync<JsonElement>(atMonth), "work-labor");
        Assert.Equal("ok", shown.GetProperty("state").GetString());
        Assert.Equal("2026-11-01", shown.GetProperty("month").GetString());
        var labor = shown.GetProperty("labor");
        Assert.Equal(6m, labor.GetProperty("estimatedHours").GetDecimal());
        Assert.Equal(8m, labor.GetProperty("actualHours").GetDecimal());
        Assert.Equal(4m, Assert.Single(labor.GetProperty("offEstimateWorks").EnumerateArray()).GetProperty("volume").GetDecimal());
        var bySection = Strip(await executive.GetFromJsonAsync<JsonElement>(atMonth + $"&section={section}"), "work-labor");
        Assert.True(bySection.GetProperty("labor").GetProperty("distributed").GetBoolean());
        Assert.Contains("расчётная", bySection.GetProperty("text").GetString());

        var noReport = await manager.GetFromJsonAsync<JsonElement>(path + "?laborMonth=2026-10-01");
        Assert.Equal("no-accepted", Strip(noReport, "work-labor").GetProperty("state").GetString());
        var denied = Strip(await hidden.GetFromJsonAsync<JsonElement>(atMonth), "work-labor");
        Assert.Equal(PlanModule.LaborRead, denied.GetProperty("permission").GetString());
        Assert.False(denied.TryGetProperty("labor", out _));
        var withoutReportRight = Strip(await factsOnly.GetFromJsonAsync<JsonElement>(atMonth), "work-labor");
        Assert.Equal("hidden", withoutReportRight.GetProperty("state").GetString());
        Assert.Equal(WorkModule.ReportRead, withoutReportRight.GetProperty("permission").GetString());
        Assert.Contains(WorkModule.ReportRead, withoutReportRight.GetProperty("text").GetString());
        Assert.False(withoutReportRight.TryGetProperty("labor", out _));
        var outsideReach = await otherSite.GetFromJsonAsync<JsonElement>(atMonth);
        Assert.DoesNotContain(outsideReach.GetProperty("strips").EnumerateArray(),
            x => x.GetProperty("code").GetString() == "work-labor");
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.GetAsync(path + "?laborMonth=2026-11-02")).StatusCode);
    }

    [Fact]
    public async Task Current_crew_fact_is_published_once_after_acceptance_and_invalidated_by_late_submission()
    {
        var (_, manager) = await crew.Reports.ManagerAsync("Менеджер", crew.Site, WorkModule.CrewApprove);
        var (member, late) = await crew.Reports.InstallerAsync("Опоздавший участник");
        await crew.AssignAsync(member);
        var lateId = Guid.NewGuid();
        var draft = await CrewWorld.CreateAsync(late, crew.Body(lateId, Guid.NewGuid(), 100, "member"));
        var (first, a) = await SubmitAsync(120, "Первый монтажник", participants: [member]);
        var (second, b) = await SubmitAsync(120, "Второй монтажник", participants: [member]);
        var (third, c) = await SubmitAsync(100, "Третий монтажник", participants: [member]);
        await ApproveAsync(manager);
        await AcceptAsync(manager, first, a);
        await AcceptAsync(manager, second, b);

        using var reader = await GrantedSignIn.WithPermissionsAsync(host, WorkModule.FactsRead);
        var waiting = await ReadAsync(reader);
        AssertState(waiting, "partial");
        Assert.Empty(waiting.GetProperty("rows").EnumerateArray());
        var diagnostics = waiting.GetProperty("factDiagnostics");
        Assert.Equal(1, diagnostics.GetProperty("awaitingAcceptance").GetInt32());
        Assert.Equal(1, diagnostics.GetProperty("excludedCrewGroups").GetInt32());
        AssertCrewReason(waiting, "AwaitingAcceptance", "awaiting-acceptance");

        await AcceptAsync(manager, third, c);
        var shown = await ReadAsync(reader);
        AssertState(shown, "ok");
        Assert.Equal(120m, FactVolume(shown));
        Assert.Equal(1, shown.GetProperty("factDiagnostics").GetProperty("unsubmitted").GetInt32());
        Assert.Equal("ok", Strip(shown, "fact-unsubmitted").GetProperty("state").GetString());
        Assert.False(Assert.Single(shown.GetProperty("rows").EnumerateArray())
            .GetProperty("cells").TryGetProperty("plan:estimate", out _));

        var json = shown.GetRawText();
        Assert.DoesNotContain("Первый монтажник", json);
        Assert.DoesNotContain("Опоздавший участник", json);
        foreach (var id in new[] { first, second, third, lateId, member })
            Assert.DoesNotContain(id.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reportId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("employeeId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hours", json, StringComparison.OrdinalIgnoreCase);
        Assert.All(shown.GetProperty("strips").EnumerateArray(), strip => Assert.Equal(JsonValueKind.Null, strip.GetProperty("link").ValueKind));
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"/api/work/reports/{first}")).StatusCode);

        await CrewWorld.SubmitAsync(late, lateId, draft);
        var stale = await ReadAsync(reader);
        AssertState(stale, "partial");
        Assert.Empty(stale.GetProperty("rows").EnumerateArray());
        AssertCrewReason(stale, "Stale", "stale");
    }

    [Fact]
    public async Task Section_filter_keeps_ambiguity_and_preserves_unrelated_accepted_fact_without_estimates()
    {
        var (_, manager) = await crew.Reports.ManagerAsync("Менеджер", crew.Site, WorkModule.CrewApprove);
        var seed = new WorkTypeSeed(host);
        var firstSection = (await seed.SendAsync(new CreateSectionCommand(crew.Site, "Корпус А"))).Id;
        var secondSection = (await seed.SendAsync(new CreateSectionCommand(crew.Site, "Корпус Б"))).Id;
        var (first, a) = await SubmitAsync(120, "Первый монтажник", firstSection);
        var (second, b) = await SubmitAsync(100, "Второй монтажник", secondSection);
        await AcceptAsync(manager, first, a);
        await AcceptAsync(manager, second, b);
        await ApproveAsync(manager);

        var (_, installer) = await crew.Reports.InstallerAsync("Обычная работа");
        var id = Guid.NewGuid();
        var ordinary = await CrewWorld.CreateAsync(installer, new
        {
            id, constructionId = crew.Site, date = ReportWorld.Day16, hours = 8m,
            lines = new[] { new { id = Guid.NewGuid(), workTypeId = crew.Reports.WorkType, unitId = crew.Reports.Unit,
                sectionId = firstSection, volume = 20m } },
        });
        await AcceptAsync(manager, id, await CrewWorld.SubmitAsync(installer, id, ordinary));

        using var reader = await GrantedSignIn.WithPermissionsAsync(host, WorkModule.FactsRead);
        var whole = await ReadAsync(reader);
        var section = await ReadAsync(reader, firstSection);
        AssertState(section, "partial");
        Assert.Equal(20m, FactVolume(section));
        AssertCrewReason(section, "MultipleKeys", "multiple-keys");
        var diagnostics = section.GetProperty("factDiagnostics");
        Assert.Equal(whole.GetProperty("factDiagnostics").GetRawText(), diagnostics.GetRawText());
        var scope = diagnostics.GetProperty("scope");
        Assert.Equal(crew.Site, scope.GetProperty("constructionId").GetGuid());
        Assert.Equal(JsonValueKind.Null, scope.GetProperty("from").ValueKind);
        Assert.Equal(JsonValueKind.Null, scope.GetProperty("to").ValueKind);
        Assert.Contains("по всей стройке за весь период", Strip(section, "fact-scope").GetProperty("text").GetString());
    }

    private async Task<(Guid Id, JsonElement Submitted)> SubmitAsync(decimal volume, string name, Guid? section = null,
        Guid[]? participants = null)
    {
        var (_, installer) = await crew.Reports.InstallerAsync(name);
        var id = Guid.NewGuid();
        var draft = await CrewWorld.CreateAsync(installer, crew.Body(id, Guid.NewGuid(), volume, section: section, participants: participants));
        return (id, await CrewWorld.SubmitAsync(installer, id, draft));
    }

    private async Task ApproveAsync(HttpClient manager)
    {
        var group = Assert.Single(await crew.GroupsAsync(manager));
        await CrewWorld.JsonAsync(await CrewWorld.ApproveAsync(manager, group.GetProperty("id").GetGuid(), ReviewWorld.Version(group)));
    }

    private static async Task AcceptAsync(HttpClient manager, Guid id, JsonElement submitted) =>
        await CrewWorld.JsonAsync(await ReviewWorld.AcceptAsync(manager, id, ReviewWorld.Version(submitted)));

    private Task<JsonElement> ReadAsync(HttpClient reader, Guid? section = null) =>
        reader.GetFromJsonAsync<JsonElement>($"/api/constructions/{crew.Site}/progress" + (section is { } id ? $"?section={id}" : ""));

    private static decimal FactVolume(JsonElement progress) => Assert.Single(progress.GetProperty("rows").EnumerateArray())
        .GetProperty("cells").GetProperty("fact").GetProperty("volume").GetDecimal();

    private static JsonElement Strip(JsonElement progress, string code) =>
        Assert.Single(progress.GetProperty("strips").EnumerateArray(), strip => strip.GetProperty("code").GetString() == code);

    private static void AssertState(JsonElement progress, string state) => Assert.Equal(state,
        Assert.Single(progress.GetProperty("columns").EnumerateArray(), c => c.GetProperty("code").GetString() == "fact")
            .GetProperty("state").GetString());

    private static void AssertCrewReason(JsonElement progress, string reason, string code)
    {
        var exclusion = Assert.Single(progress.GetProperty("factDiagnostics").GetProperty("crewExclusions").EnumerateArray());
        Assert.Equal(reason, exclusion.GetProperty("reason").GetString());
        Assert.Equal(1, exclusion.GetProperty("groups").GetInt32());
        Assert.Equal("partial", Strip(progress, $"fact-crew-{code}").GetProperty("state").GetString());
    }
}
