using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Endpoints;

namespace BHS.CRG.Tests.Integration;

[Collection(AssignmentHost.Collection)]
public class ReviewCardTests(AssignmentHost host) : IAsyncLifetime
{
    private readonly ReviewWorld world = new(host);

    public Task InitializeAsync() => world.SeedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Чтение_руководителя_не_расширяет_охват_действий()
    {
        var (_, manager) = await world.Reports.ManagerAsync("Менеджер и руководитель", world.SiteA, WorkModule.ReportRead);
        var (_, local) = await world.Reports.ManagerAsync("Только менеджер", world.SiteA);
        var (_, installer) = await world.Reports.InstallerAsync("Монтажник двух строек");
        var (_, executive) = await world.Reports.Assignments.UserAsync("Руководитель", WorkModule.ReportRead);
        var (own, foreign) = (Guid.NewGuid(), Guid.NewGuid());
        var submitted = await world.SubmittedAsync(installer, own, world.SiteA);
        await world.SubmittedAsync(installer, foreign, world.SiteB);

        var ownCard = await manager.GetFromJsonAsync<JsonElement>(Url(own));
        Assert.Equal(["accept", "return", "edit"], ownCard.GetProperty("actions").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(ReviewWorld.Version(submitted), ReviewWorld.Version(ownCard.GetProperty("report")));
        Assert.Equal("Монтажник двух строек", ownCard.GetProperty("employeeName").GetString());
        Assert.Empty((await executive.GetFromJsonAsync<JsonElement>(Url(own))).GetProperty("actions").EnumerateArray());
        Assert.Empty((await manager.GetFromJsonAsync<JsonElement>(Url(foreign))).GetProperty("actions").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await installer.GetAsync(Url(own))).StatusCode);
        var outside = await local.GetAsync(Url(foreign));
        var missing = await local.GetAsync(Url(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(await AssignmentWorld.ErrorAsync(missing), await AssignmentWorld.ErrorAsync(outside));
    }

    [Fact]
    public async Task Черновик_отдаёт_только_присутствие_и_момент_автоподачи()
    {
        var (_, manager) = await world.Reports.ManagerAsync("Менеджер", world.SiteA);
        var (_, installer) = await world.Reports.InstallerAsync("Монтажник");
        var id = Guid.NewGuid();
        var created = await installer.PostAsJsonAsync(ReportEndpoints.Path,
            world.Reports.Draft(id, world.SiteA, lineId: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var draft = await ReportWorld.JsonAsync(created);

        var card = await manager.GetFromJsonAsync<JsonElement>(Url(id));
        Assert.Equal("draft", card.GetProperty("status").GetString());
        Assert.Equal(draft.GetProperty("autoSubmitAt").GetDateTimeOffset(), card.GetProperty("autoSubmitAt").GetDateTimeOffset());
        Assert.Empty(card.GetProperty("actions").EnumerateArray());
        Assert.Equal(new[] { "actions", "autoSubmitAt", "constructionName", "employeeName", "id", "status", "timeZoneId" },
            card.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync(Url(id) + "?number=1")).StatusCode);

        var submitted = await SubmissionTests.SubmitAsync(installer, $"{ReportEndpoints.Path}/{id}",
            ReviewWorld.Version(draft), AssignmentHost.Now);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        var after = await manager.GetFromJsonAsync<JsonElement>(Url(id));
        Assert.Single(after.GetProperty("report").GetProperty("lines").EnumerateArray());
    }

    private static string Url(Guid id) => $"/api/work/review/reports/{id}";
}
