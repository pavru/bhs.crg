using System.Net;
using System.Net.Http.Json;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using BHS.CRG.Tests.Integration;

namespace BHS.CRG.Tests.Work;

[Collection(AssignmentHost.Collection)]
public class CrewCandidatesTests(AssignmentHost host) : IAsyncLifetime
{
    private readonly CrewWorld crew = new(host);

    public Task InitializeAsync() => crew.SeedAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Only_assigned_installers_and_site_managers_can_read_names()
    {
        var (me, installer) = await crew.Reports.InstallerAsync("Монтажник");
        var local = await crew.Reports.EmployeeAsync("Участник в смене");
        var tomorrow = await crew.Reports.EmployeeAsync("Позже");
        var foreign = await crew.Reports.EmployeeAsync("Чужая стройка");
        var (_, manager) = await crew.Reports.ManagerAsync("Менеджер", crew.Site);
        await crew.AssignAsync(me);
        await crew.AssignAsync(local, from: ReportWorld.Day16, to: ReportWorld.Day16);
        await crew.AssignAsync(tomorrow, from: "2026-11-17");
        await crew.AssignAsync(foreign, crew.Reports.SiteB);

        Assert.Equal(HttpStatusCode.Forbidden, (await installer.GetAsync("/api/employees")).StatusCode);
        var url = $"/api/work/constructions/{crew.Site}/crew-candidates?date={ReportWorld.Day16}";
        foreach (var caller in new[] { installer, manager })
        {
            var response = await CrewWorld.JsonAsync(await caller.GetAsync(url));
            var items = response.GetProperty("items").EnumerateArray().ToArray();
            Assert.Contains(items, e => e.GetProperty("id").GetGuid() == local && e.GetProperty("fullName").GetString() == "Участник в смене");
            Assert.DoesNotContain(items, e => e.GetProperty("id").GetGuid() == tomorrow);
            Assert.All(items, e => Assert.Equal(new[] { "fullName", "id" }, e.EnumerateObject().Select(p => p.Name).Order()));
        }
        var denied = await CrewWorld.JsonAsync(await installer.GetAsync(url.Replace(crew.Site.ToString(), crew.Reports.SiteB.ToString())));
        Assert.Empty(denied.GetProperty("items").EnumerateArray());
        Assert.Equal("not-assigned", denied.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Entry_candidates_require_review_and_use_manager_reach_without_a_personal_assignment()
    {
        var employee = await crew.Reports.EmployeeAsync("Участник на дату отчёта");
        var later = await crew.Reports.EmployeeAsync("Участник следующей смены");
        var foreign = await crew.Reports.EmployeeAsync("Участник другой стройки");
        await crew.AssignAsync(employee, from: ReportWorld.Day16, to: ReportWorld.Day16);
        await crew.AssignAsync(later, from: "2026-11-17");
        await crew.AssignAsync(foreign, crew.Reports.SiteB);
        var (_, all) = await crew.Reports.Assignments.UserAsync("Полный охват", WorkModule.ReportReview, WorkModule.SitesAll);
        var (_, noReview) = await crew.Reports.Assignments.UserAsync("Без права ввода", WorkModule.SitesAll);
        var (_, outside) = await crew.Reports.ManagerAsync("Менеджер другой стройки", crew.Reports.SiteB);

        var url = $"/api/work/entry/sites/{crew.Site}/crew-candidates?date={ReportWorld.Day16}";
        var allowed = await CrewWorld.JsonAsync(await all.GetAsync(url));
        Assert.Equal([employee], allowed.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
        var denied = await CrewWorld.JsonAsync(await outside.GetAsync(url));
        Assert.Empty(denied.GetProperty("items").EnumerateArray());
        Assert.Equal("not-assigned", denied.GetProperty("reason").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await noReview.GetAsync(url)).StatusCode);
    }

    [Theory]
    [InlineData("former-manager", "2026-10-15")]
    [InlineData("archived-installer", "2026-10-31")]
    [InlineData("dismissed-installer", "2026-10-31")]
    public async Task Assignment_alone_does_not_open_candidates(string scenario, string date)
    {
        var formerManager = scenario == "former-manager";
        var assignments = crew.Reports.Assignments;
        var employee = await assignments.EmployeeAsync(crew.Reports.EmployeeType, "Спрашивающий",
            dismissed: scenario == "dismissed-installer" ? date : null);
        var candidate = await crew.Reports.EmployeeAsync("Кандидат");
        var (user, client) = await assignments.UserAsync("Спрашивающий", formerManager ? WorkModule.ReportReview : WorkModule.ReportOwn);
        await assignments.LinkAsync(employee, user);
        await assignments.AssignedAsync(employee, crew.Site, formerManager ? AssignmentKind.Manager : AssignmentKind.Installer,
            "2026-10-01", formerManager ? "2026-10-20" : "2026-11-15");
        await crew.AssignAsync(candidate, from: "2026-10-01");
        if (scenario == "archived-installer") await assignments.ArchiveAsync(employee);

        var result = await CrewWorld.JsonAsync(await client.GetAsync($"/api/work/constructions/{crew.Site}/crew-candidates?date={date}"));
        Assert.Empty(result.GetProperty("items").EnumerateArray());
        Assert.Equal("not-assigned", result.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Saving_rejects_unassigned_participants_and_preserves_explicit_role_and_volume()
    {
        var (lead, installer) = await crew.Reports.InstallerAsync("Бригадир");
        var (member, worker) = await crew.Reports.InstallerAsync("Участник");
        await crew.AssignAsync(lead);
        var report = Guid.NewGuid();
        var line = Guid.NewGuid();
        var body = crew.Body(report, line, 120, participants: [member]);
        var rejected = await installer.PostAsJsonAsync(ReportEndpoints.Path, body);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var error = await AssignmentWorld.ErrorAsync(rejected);
        Assert.Contains("Участник", error);
        Assert.Contains("16.11", error);
        Assert.Equal(0, await crew.Reports.ReportCountAsync());

        await crew.AssignAsync(member);
        var first = await CrewWorld.CreateAsync(installer, body);
        var otherId = Guid.NewGuid();
        var otherLine = Guid.NewGuid();
        var second = await CrewWorld.CreateAsync(worker, crew.Body(otherId, otherLine, 100, "member"));
        Assert.Equal(120m, first.GetProperty("lines")[0].GetProperty("volume").GetDecimal());
        Assert.Equal(100m, second.GetProperty("lines")[0].GetProperty("volume").GetDecimal());
        Assert.Equal("lead", first.GetProperty("lines")[0].GetProperty("crewRole").GetString());
        Assert.Equal("member", second.GetProperty("lines")[0].GetProperty("crewRole").GetString());
        var legacy = await CrewWorld.JsonAsync(await ReportWorld.PutAsync(worker, $"{ReportEndpoints.Path}/{otherId}",
            ReviewWorld.Version(second), crew.Body(otherId, otherLine, 100, role: null)));
        Assert.Equal("member", legacy.GetProperty("lines")[0].GetProperty("crewRole").GetString());
    }
}
