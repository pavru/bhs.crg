using System.Net;
using System.Text.Json;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Tests.Integration;

namespace BHS.CRG.Tests.Work;

[Collection(WorkLaborCollection.Name)]
public class WorkLaborTests(WorkLaborHost host) : IAsyncLifetime
{
    private readonly WorkLaborWorld world = new(host);

    public Task InitializeAsync() => world.SeedAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Distribution_keeps_report_hours_and_accounts_for_missing_and_zero_norms_separately()
    {
        var site = world.Reports.SiteA;
        var sectionA = await world.SectionAsync(site, "Корпус 1");
        var sectionB = await world.SectionAsync(site, "Корпус 2");
        var zeroWeight = await world.SectionAsync(site, "Нулевой вес");
        var (_, manager) = await world.Reports.ManagerAsync("Менеджер", site, PlanModule.LaborRead);
        var employee = await world.Reports.EmployeeAsync("Первый");
        await world.NormAsync(site, sectionA, .5m);
        await world.NormAsync(site, sectionB, .5m);
        await world.NormAsync(site, zeroWeight, 0m);
        await world.NormAsync(site, null, .25m);
        await world.ReportAsync(site, employee, 10m, ReportStatus.Accepted, WorkLaborWorld.Day,
            world.Line(12, sectionA), world.Line(4, sectionB), world.Line(1, sectionA, world.Reports.WorkTypeNeedsPlace));
        await world.ReportAsync(site, employee, 8m, ReportStatus.Accepted, WorkLaborWorld.Day.AddDays(1),
            world.Line(10, type: world.Reports.WorkTypeNeedsPlace));
        await world.ReportAsync(site, employee, 6m, ReportStatus.Accepted, WorkLaborWorld.Day.AddDays(2), world.Line(12, zeroWeight));
        await world.ReportAsync(site, employee, 20m, ReportStatus.Submitted, WorkLaborWorld.Day.AddDays(3), world.Line(100, sectionA));
        await world.ReportAsync(site, employee, 3m, ReportStatus.Accepted, WorkLaborWorld.Day.AddDays(4), world.Line(4));

        var total = await ReadAsync(manager, WorkLaborWorld.Estimate(site));
        Assert.Equal("ok", State(total));
        Assert.Equal(9m, Number(total, "estimatedHours"));
        Assert.Equal(13m, Number(total, "actualHours"));
        Assert.Equal(8m, Number(total.GetProperty("offEstimate"), "hours"));
        Assert.Equal(6m, Number(total, "unallocatedHours"));
        var first = await ReadAsync(manager, WorkLaborWorld.Estimate(site, sectionA));
        var second = await ReadAsync(manager, WorkLaborWorld.Estimate(site, sectionB));
        Assert.True(first.GetProperty("distributed").GetBoolean());
        Assert.Equal(7.5m, Number(first, "actualHours"));
        Assert.Equal(2.5m, Number(second, "actualHours"));
        Assert.Equal(3m, Number(total, "unsectionedHours"));
        Assert.Equal(27m, Number(first, "actualHours") + Number(second, "actualHours") + Number(total, "unsectionedHours")
            + Number(total.GetProperty("offEstimate"), "hours") + Number(total, "unallocatedHours"));
        Assert.Equal(1m, Number(Assert.Single(first.GetProperty("offEstimate").GetProperty("works").EnumerateArray()), "volume"));
    }

    [Fact]
    public async Task Employee_percentage_and_construction_filter_follow_current_manager_assignments()
    {
        var a = world.Reports.SiteA;
        var b = world.Reports.SiteB;
        var (_, manager) = await world.Reports.ManagerAsync("Менеджер", a, PlanModule.LaborRead);
        var employee = await world.Reports.EmployeeAsync("На двух стройках");
        var other = await world.Reports.EmployeeAsync("Только на другой стройке");
        await world.NormAsync(a, null, .5m);
        await world.NormAsync(b, null, .5m);
        await world.ReportAsync(a, employee, 8, ReportStatus.Accepted, WorkLaborWorld.Day, world.Line(12));
        await world.ReportAsync(b, employee, 10, ReportStatus.Accepted, WorkLaborWorld.Day, world.Line(100));
        await world.ReportAsync(b, other, 12, ReportStatus.Accepted, WorkLaborWorld.Day, world.Line(100));
        var url = "/api/work/labor/productivity?byEmployee=true&from=2026-11-16&to=2026-11-16";
        var result = await ReadAsync(manager, url);
        var person = Assert.Single(result.GetProperty("employees").EnumerateArray());
        Assert.Equal(employee, person.GetProperty("employeeId").GetGuid());
        Assert.Equal(75m, Number(person.GetProperty("comparison"), "percent"));
        Assert.True(person.GetProperty("comparison").GetProperty("distributed").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync(WorkLaborWorld.Estimate(b))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync(url + $"&constructionId={b}")).StatusCode);
    }

    [Fact]
    public async Task Real_plan_provider_distinguishes_hidden_no_accepted_and_no_norms()
    {
        var site = world.Reports.SiteA;
        var (_, manager) = await world.Reports.ManagerAsync("Доступ к нормам", site, PlanModule.LaborRead);
        var (_, noNormRight) = await world.Reports.ManagerAsync("Только приёмка", site);
        var (_, installer) = await world.Reports.InstallerAsync("Монтажник");
        var url = WorkLaborWorld.Estimate(site);
        Assert.Equal(HttpStatusCode.Forbidden, (await installer.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await installer.GetAsync(world.Productivity())).StatusCode);
        var hidden = await ReadAsync(noNormRight, url);
        Assert.Equal("hidden", State(hidden));
        Assert.Equal(PlanModule.LaborRead, hidden.GetProperty("permission").GetString());
        Assert.Equal(JsonValueKind.Null, hidden.GetProperty("actualHours").ValueKind);
        Assert.Equal("no-accepted", State(await ReadAsync(manager, url)));

        var employee = await world.Reports.EmployeeAsync("Без нормы");
        await world.ReportAsync(site, employee, 8, ReportStatus.Accepted, WorkLaborWorld.Day, world.Line(12));
        var noNorms = await ReadAsync(manager, url);
        Assert.Equal("no-norms", State(noNorms));
        Assert.Equal(JsonValueKind.Null, noNorms.GetProperty("estimatedHours").ValueKind);
        Assert.Equal(8m, Number(noNorms.GetProperty("offEstimate"), "hours"));
    }

    [Fact]
    public async Task Built_in_roles_show_the_narrow_labor_permission_and_executive_reads_using_estimate_permission()
    {
        var manager = await AccessProbe.SignInAsync(host, "ProjectManager", "work28");
        var executive = await AccessProbe.SignInAsync(host, "Executive", "work28");
        var installer = await AccessProbe.SignInAsync(host, "Installer", "work28");
        Assert.Contains(PlanModule.LaborRead, (await AccessProbe.AccessAsync(manager)).Permissions);
        Assert.Contains(WorkModule.ReportReview, (await AccessProbe.AccessAsync(manager)).Permissions);
        var read = (await AccessProbe.AccessAsync(executive)).Permissions;
        Assert.DoesNotContain(PlanModule.LaborRead, read);
        Assert.Contains(PlanModule.EstimateRead, read);
        Assert.Contains(WorkModule.ReportRead, read);
        Assert.DoesNotContain(PlanModule.LaborRead, (await AccessProbe.AccessAsync(installer)).Permissions);
        Assert.Equal("no-accepted", State(await ReadAsync(executive, WorkLaborWorld.Estimate(world.Reports.SiteB))));
    }

    internal static async Task<JsonElement> ReadAsync(HttpClient client, string url) =>
        await CrewWorld.JsonAsync(await client.GetAsync(url));

    internal static string? State(JsonElement value) => value.GetProperty("state").GetString();
    internal static decimal Number(JsonElement value, string name) => value.GetProperty(name).GetDecimal();
}
