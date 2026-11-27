using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Work;
using BHS.CRG.Tests.Common;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;
using BHS.CRG.Tests.Work;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static BHS.CRG.Tests.Plan.OfferDerivativeScenario;
using static BHS.CRG.Tests.Plan.ScheduleFactScenario;
using static BHS.CRG.Tests.Plan.ScheduleScenario;

namespace BHS.CRG.Tests.Plan;

[Collection(PlanOnlyCollection.Name)]
public class ScheduleFactTests(PlanOnlyHost host)
{
    [Fact]
    public async Task График_без_Work_явно_отвечает_что_учёт_не_подключён()
    {
        var scene = await CreateAsync(host);
        using var editor = scene.Editor;
        var schedule = await scene.BuildAsync();
        await scene.ManualAsync(schedule);

        using var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.ScheduleRead);
        var opened = await ReadAsync(reader.GetAsync(ScheduleUrl(schedule)));
        AssertFact(Assert.Single(Works(opened)), "not-connected", null);
        Assert.Equal("not-connected", Assert.Single(Works(opened)).GetProperty("productivity").GetProperty("state").GetString());
        Assert.False(opened.TryGetProperty("factDiagnostics", out _));
    }
}

public class ScheduleFactProbeTests(ScheduleFactProbeHost host) : IClassFixture<ScheduleFactProbeHost>
{
    [Fact]
    public async Task Проба_публикует_текущий_объём_только_нужной_позиции_и_точного_раздела()
    {
        var seed = new WorkTypeSeed(host);
        var (unitType, workType) = await seed.ClassifierAsync();
        var unit = await seed.RecordAsync(unitType.Id, "м");
        var kind = await seed.RecordAsync(workType.Id, "Кабель", WorkTypeSeed.WorkTypeData("01", "Кабель", unit));
        var site = (await seed.SendAsync(new CreateConstructionCommand("Проба графика", Guid.NewGuid()))).Id;
        var section = (await seed.SendAsync(new CreateSectionCommand(site, "Корпус"))).Id;
        using var editor = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.ScheduleRead, PlanModule.ScheduleEdit);
        var schedule = await BuildAsync(editor, site);
        var whole = await AddWorkAsync(editor, schedule, kind.Id);
        var local = await AddWorkAsync(editor, schedule, kind.Id, section);

        var wholeItem = whole.GetProperty("planItemId").GetGuid();
        var localItem = local.GetProperty("planItemId").GetGuid();
        var day = new DateOnly(2026, 11, 17);
        var scope = new ActualWorksScope(site, null, null);
        var probe = host.Services.GetRequiredService<ActualWorksProbeData>();
        probe.Facts[site] = new([
            new(wholeItem, day, "day", null, 5),
            new(wholeItem, day.AddDays(1), "night", null, 7),
            new(localItem, day, "day", section, 30),
            new(wholeItem, day, "night", section, 90),
            new(Guid.NewGuid(), day, "day", section, 900),
        ], new(scope, 0, 0, [], 0, 1));

        using var facts = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.ScheduleRead, ActualWorksProbeModule.FactsRead);
        var opened = await ReadAsync(facts.GetAsync(ScheduleUrl(schedule)));
        AssertFact(WorkById(opened, whole), "ok", 12);
        AssertFact(WorkById(opened, local), "ok", 30);
        Assert.Equal(1, opened.GetProperty("factDiagnostics").GetProperty("unsubmitted").GetInt32());

        probe.Facts[site] = new([new(localItem, day, "day", section, 0)],
            new(scope, 1, 1, [new(CrewFactExclusion.MultipleKeys, 1)], 0, 0));
        var partial = await ReadAsync(facts.GetAsync(ScheduleUrl(schedule)));
        AssertFact(WorkById(partial, whole), "partial", null);
        AssertFact(WorkById(partial, local), "partial", 0);
        var diagnostics = partial.GetProperty("factDiagnostics");
        AssertScope(diagnostics, site);
        Assert.Equal("MultipleKeys", Assert.Single(diagnostics.GetProperty("crewExclusions").EnumerateArray())
            .GetProperty("reason").GetString());

        using var scheduleOnly = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.ScheduleRead);
        var hidden = await ReadAsync(scheduleOnly.GetAsync(ScheduleUrl(schedule)));
        Assert.All(Works(hidden), work => AssertFact(work, "hidden", null, ActualWorksProbeModule.FactsRead));
        Assert.All(Works(hidden), work =>
        {
            var productivity = work.GetProperty("productivity");
            Assert.Equal("hidden", productivity.GetProperty("state").GetString());
            Assert.Equal(WorkModule.FactsRead, productivity.GetProperty("permission").GetString());
            Assert.False(productivity.TryGetProperty("months", out _));
        });
        Assert.False(hidden.TryGetProperty("factDiagnostics", out _));
        using var workOnly = await GrantedSignIn.WithPermissionsAsync(host, ActualWorksProbeModule.FactsRead);
        using var refused = await workOnly.GetAsync(ScheduleUrl(schedule));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }
}

public class ScheduleFactRealWorkTests(ScheduleFactWorkHost host) : IClassFixture<ScheduleFactWorkHost>
{
    [Fact]
    public async Task Настоящий_Work_показывает_принятие_и_исправление_а_не_историю_отчётов()
    {
        var world = new ReportWorld(host);
        await world.SeedAsync();
        var section = (await new WorkTypeSeed(host).SendAsync(new CreateSectionCommand(world.SiteA, "Корпус"))).Id;
        using var editor = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.ScheduleRead, PlanModule.ScheduleEdit);
        var schedule = await BuildAsync(editor, world.SiteA);
        var local = await AddWorkAsync(editor, schedule, world.WorkType, section);
        var whole = await AddWorkAsync(editor, schedule, world.WorkType);

        var (_, installer) = await world.InstallerAsync("Монтажник факта");
        var (_, manager) = await world.ManagerAsync("Менеджер факта", world.SiteA);
        using var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.ScheduleRead, WorkModule.FactsRead);

        var id = Guid.NewGuid();
        var line = Guid.NewGuid();
        object Report(decimal volume, string? reason = null) => new
        {
            id, reason, constructionId = world.SiteA, date = WorkPeriodWorld.OctoberEnd, shift = "day", hours = 8,
            lines = new[] { new { id = line, workTypeId = world.WorkType, unitId = world.Unit, sectionId = section, volume } },
        };
        var draft = await CrewWorld.CreateAsync(installer, Report(120m));
        var submitted = await CrewWorld.JsonAsync(await SubmissionTests.SubmitAsync(installer,
            $"/api/work/reports/{id}", ReviewWorld.Version(draft), AssignmentHost.Now.AddHours(3)));
        var pending = await ReadAsync(reader.GetAsync(ScheduleUrl(schedule)));
        AssertFact(WorkById(pending, local), "partial", null);
        var diagnostics = pending.GetProperty("factDiagnostics");
        Assert.Equal(1, diagnostics.GetProperty("awaitingAcceptance").GetInt32());
        AssertScope(diagnostics, world.SiteA);

        var accepted = await CrewWorld.JsonAsync(await ReviewWorld.AcceptAsync(manager, id, ReviewWorld.Version(submitted)));
        var shown = await ReadAsync(reader.GetAsync(ScheduleUrl(schedule)));
        AssertFact(WorkById(shown, local), "ok", 120);
        AssertFact(WorkById(shown, whole), "ok", null);
        var productivity = WorkById(shown, local).GetProperty("productivity");
        Assert.Equal("too-little-data", productivity.GetProperty("state").GetString());
        var month = Assert.Single(productivity.GetProperty("months").EnumerateArray()).GetProperty("productivity");
        Assert.Equal(1, month.GetProperty("sample").GetProperty("reports").GetInt32());
        Assert.False(month.TryGetProperty("volumePerHour", out _));

        host.ClosureTime = AssignmentHost.Now.AddDays(5);
        using var closer = await new WorkPeriodWorld(host).CloserAsync();
        await WorkPeriodWorld.CloseAsync(closer);
        using var corrected = await ReportCorrectionTests.PostAsync(manager, id, ReviewWorld.Version(accepted), Report(90, "Исправлено по акту"));
        Assert.True(corrected.IsSuccessStatusCode, await corrected.Content.ReadAsStringAsync());
        var current = await ReadAsync(reader.GetAsync(ScheduleUrl(schedule)));
        AssertFact(WorkById(current, local), "ok", 90);
        Assert.DoesNotContain(id.ToString(), current.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Монтажник факта", current.GetRawText());
        Assert.DoesNotContain("hours", current.GetRawText(), StringComparison.OrdinalIgnoreCase);
        using var refused = await reader.GetAsync($"/api/work/reports/{id}");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }
}

public sealed class ScheduleFactProbeHost : ModuleOnlyHost
{
    protected override string ModuleCode => "plan";
    protected override string DatabaseSuffix => "schedule_fact_probe";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(ActualWorksProbeModule.Enable);
    }
}

public sealed class ScheduleFactWorkHost : AssignmentHost
{
    protected override string ModuleCode => "plan,work";
    protected override string DatabaseSuffix => "plan_schedule_fact_work";
}

internal static class ScheduleFactScenario
{
    internal static Task<JsonElement> BuildAsync(HttpClient editor, Guid site) => ReadAsync(editor.PostAsJsonAsync(Root,
        new { constructionId = site, creationKey = Guid.NewGuid(), name = "Электромонтаж" }), HttpStatusCode.Created);

    internal static Task<JsonElement> AddWorkAsync(HttpClient editor, JsonElement schedule, Guid workTypeId, Guid? sectionId = null) =>
        ReadAsync(editor.PostAsJsonAsync($"{ScheduleUrl(schedule)}/works", new { workTypeId, sectionId, work = new { volume = 1 } }));

    internal static JsonElement WorkById(JsonElement schedule, JsonElement work) => Assert.Single(Works(schedule), w => Id(w) == Id(work));

    internal static void AssertFact(JsonElement work, string state, decimal? volume, string? permission = null)
    {
        var fact = work.GetProperty("fact");
        Assert.Equal(state, fact.GetProperty("state").GetString());
        var actual = fact.GetProperty("volume");
        Assert.Equal(volume, actual.ValueKind == JsonValueKind.Null ? null : actual.GetDecimal());
        Assert.Equal(permission, fact.TryGetProperty("permission", out var right) ? right.GetString() : null);
    }

    internal static void AssertScope(JsonElement diagnostics, Guid site)
    {
        var scope = diagnostics.GetProperty("scope");
        Assert.Equal(site, scope.GetProperty("constructionId").GetGuid());
        Assert.Equal(JsonValueKind.Null, scope.GetProperty("from").ValueKind);
        Assert.Equal(JsonValueKind.Null, scope.GetProperty("to").ValueKind);
    }
}
