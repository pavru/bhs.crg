using System.Net;
using BHS.CRG.Modules.Work;
using BHS.CRG.Tests.Integration;

namespace BHS.CRG.Tests.Work;

[Collection(AssignmentHost.Collection)]
public class CrewGateTests(AssignmentHost host) : IAsyncLifetime
{
    private readonly CrewWorld crew = new(host);

    public Task InitializeAsync() => crew.SeedAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Approval_requires_permission_reach_and_version_and_read_all_only_grants_reading()
    {
        var (_, installer) = await crew.Reports.InstallerAsync("Монтажник");
        var (_, local) = await crew.Reports.ManagerAsync("Местный менеджер", crew.Site, WorkModule.CrewApprove);
        var (_, other) = await crew.Reports.ManagerAsync("Другой менеджер", crew.Reports.SiteB, WorkModule.CrewApprove);
        var (_, reviewer) = await crew.Reports.ManagerAsync("Без права", crew.Site);
        var (_, executive) = await crew.Reports.Assignments.UserAsync("Руководитель", WorkModule.ReportRead);
        var (_, allSites) = await crew.Reports.Assignments.UserAsync("Все стройки", WorkModule.CrewApprove, WorkModule.SitesAll);
        await crew.SubmittedAsync(installer, Guid.NewGuid(), Guid.NewGuid(), 120);
        var group = Assert.Single(await crew.GroupsAsync(local));
        var id = group.GetProperty("id").GetGuid();
        var seen = ReviewWorld.Version(group);
        Assert.NotNull(seen);
        var url = $"{CrewWorld.Path}/{id}";

        Assert.Equal(HttpStatusCode.Forbidden, (await installer.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CrewWorld.ApproveAsync(reviewer, id, seen)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(url)).StatusCode);
        Assert.Empty(await crew.GroupsAsync(other));
        Assert.Equal(HttpStatusCode.NotFound, (await CrewWorld.ApproveAsync(other, id, seen)).StatusCode);
        Assert.Empty((await CrewWorld.JsonAsync(await executive.GetAsync(url))).GetProperty("actions").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await CrewWorld.ApproveAsync(local, id, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await CrewWorld.ApproveAsync(local, id, "0")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CrewWorld.ApproveAsync(allSites, id, seen)).StatusCode);
    }
}

[Collection(PlanOnlyCollection.Name)]
public class CrewDisabledGateTests(PlanOnlyHost host)
{
    [Fact]
    public async Task Approval_is_behind_the_work_module_gate()
    {
        using var client = host.CreateClient();
        var result = await CrewWorld.ApproveAsync(client, Guid.NewGuid(), "1");
        var body = await CrewWorld.JsonAsync(result, HttpStatusCode.NotImplemented);
        Assert.Equal("work", body.GetProperty("module").GetString());
    }
}
