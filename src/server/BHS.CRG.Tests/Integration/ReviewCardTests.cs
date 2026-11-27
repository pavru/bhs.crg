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

    public Task DisposeAsync()
    {
        host.ClosureTime = null;
        return Task.CompletedTask;
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Закрытая_действующая_карточка_предлагает_исправление_только_в_охвате_менеджера(bool company)
    {
        var periods = new WorkPeriodWorld(host);
        var site = world.SiteA;
        var (_, installer) = await world.Reports.InstallerAsync("Монтажник");
        var (_, manager) = await world.Reports.ManagerAsync("Менеджер", site);
        var (_, siteB) = await world.Reports.ManagerAsync("Менеджер другой стройки", world.SiteB);
        var (_, managerReader) = await world.Reports.ManagerAsync("Менеджер другой стройки с чтением", world.SiteB, WorkModule.ReportRead);
        var (_, reader) = await world.Reports.Assignments.UserAsync("Руководитель", WorkModule.ReportRead);
        var (_, all) = await world.Reports.Assignments.UserAsync("Все стройки", WorkModule.ReportReview, WorkModule.SitesAll);
        var id = Guid.NewGuid();
        var submitted = await world.SubmittedAsync(installer, id, site, WorkPeriodWorld.OctoberEnd, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.OK, (await ReviewWorld.AcceptAsync(manager, id, ReviewWorld.Version(submitted))).StatusCode);
        host.ClosureTime = AssignmentHost.Now.AddDays(5);
        await WorkPeriodWorld.CloseAsync(await periods.CloserAsync(), site: company ? null : site);

        foreach (var allowed in new[] { manager, all })
        {
            var card = await allowed.GetFromJsonAsync<JsonElement>(Url(id));
            Assert.Equal("correct", Assert.Single(card.GetProperty("actions").EnumerateArray()).GetString());
            Assert.False(card.GetProperty("report").GetProperty("acceptWouldMark").GetBoolean());
            Assert.Empty(card.GetProperty("currentRequiredMissing").EnumerateArray());
        }
        foreach (var readOnly in new[] { reader, managerReader })
            Assert.Empty((await readOnly.GetFromJsonAsync<JsonElement>(Url(id))).GetProperty("actions").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await siteB.GetAsync(Url(id))).StatusCode);
    }

    private static string Url(Guid id) => $"/api/work/review/reports/{id}";
}
