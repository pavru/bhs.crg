using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Submission;
using BHS.CRG.Modules.Work.Summary;
using BHS.CRG.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace BHS.CRG.Tests.Work;

[Collection(AssignmentHost.Collection)]
public class SummaryEndpointTests(AssignmentHost host, ITestOutputHelper output) : IAsyncLifetime
{
    private const string Url = SummaryEndpoints.Path + "?from=2026-10-30&to=2026-11-01";
    private static readonly DateOnly October31 = new(2026, 10, 31);
    private static readonly DateTimeOffset Now = AssignmentHost.Now;
    private readonly WorkTableWorld world = new(host);

    public Task InitializeAsync() => world.Assignments.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Менеджер_видит_только_свою_стройку_а_чужая_и_несуществующая_отвечают_одинаково()
    {
        var directory = await world.Assignments.DirectoryAsync();
        var local = await world.Assignments.SiteAsync("Доступная стройка");
        var foreign = await world.Assignments.SiteAsync("Другая стройка");
        var employee = await world.Assignments.EmployeeAsync(directory, "Монтажник");
        await world.Assignments.AssignedAsync(employee, local, AssignmentKind.Installer, "2026-10-31", "2026-11-01");
        await world.Assignments.AssignedAsync(employee, foreign, AssignmentKind.Installer, "2026-10-31", "2026-11-01");

        using var manager = (await world.ManagerAsync(directory, local)).Client;
        var page = await manager.GetFromJsonAsync<JsonElement>(Url);
        Assert.Equal("отчёты ваших строек (1)", page.GetProperty("boundary").GetString());
        Assert.Equal("sites", page.GetProperty("boundaryOutcome").GetString());
        Assert.Equal(local, Assert.Single(Sites(page)));
        Assert.Equal(local, Assert.Single(Rows(page).EnumerateArray()).GetProperty("constructionId").GetGuid());
        Assert.Equal(local, Assert.Single(Rows(await manager.GetFromJsonAsync<JsonElement>($"{Url}&construction={local}")).EnumerateArray())
            .GetProperty("constructionId").GetGuid());

        using var hidden = await manager.GetAsync($"{Url}&construction={foreign}");
        using var unknown = await manager.GetAsync($"{Url}&construction={Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(hidden.StatusCode, unknown.StatusCode);
        Assert.Equal(await AssignmentWorld.ErrorAsync(hidden), await AssignmentWorld.ErrorAsync(unknown));

        using var emptyManager = (await world.ManagerAsync(directory)).Client;
        var empty = await emptyManager.GetFromJsonAsync<JsonElement>(Url);
        Assert.Empty(Rows(empty).EnumerateArray());
        Assert.Empty(Sites(empty));
        Assert.Equal("not-assigned", empty.GetProperty("reason").GetString());
        Assert.Equal("not-assigned", empty.GetProperty("boundaryOutcome").GetString());

        using var allSites = (await world.Assignments.UserAsync("Менеджер всех строек", WorkModule.ReportReview, WorkModule.SitesAll)).Client;
        Assert.Equal(2, Rows(await allSites.GetFromJsonAsync<JsonElement>(Url)).GetArrayLength());
        using var installer = (await world.InstallerAsync(directory, "Монтажник с доступом к своим отчётам")).Client;
        Assert.Equal(HttpStatusCode.Forbidden, (await installer.GetAsync(Url)).StatusCode);
    }

    [Fact]
    public async Task Руководитель_видит_все_стройки_с_сутками_по_поясу_и_черновик_только_наличием()
    {
        var directory = await world.Assignments.DirectoryAsync();
        var east = await world.Assignments.SiteAsync("Владивосток", "Asia/Vladivostok");
        var west = await world.Assignments.SiteAsync("Москва");
        var employee = await world.Assignments.EmployeeAsync(directory, "Монтажник");
        foreach (var site in new[] { east, west })
            await world.Assignments.AssignedAsync(employee, site, AssignmentKind.Installer, "2026-10-31", "2026-11-01");
        var (type, unit, _) = await world.ClassifierAsync();
        var draft = await world.ReportAsync(employee, east, type, unit, "Место черновика", 7, false, 10);
        var report = MakeReport(employee, east, October31, ReportShift.Day, 0);
        await SeedReportsAsync([report]);

        using var reader = (await world.Assignments.UserAsync("Руководитель", WorkModule.ReportRead)).Client;
        var page = await reader.GetFromJsonAsync<JsonElement>(Url);
        Assert.Equal("все стройки", page.GetProperty("boundary").GetString());
        Assert.Equal("all", page.GetProperty("boundaryOutcome").GetString());
        Assert.Equal(new[] { "2026-10-30", "2026-10-31", "2026-11-01" },
            page.GetProperty("days").EnumerateArray().Select(d => d.GetString()));
        var rows = Rows(page).EnumerateArray().ToDictionary(r => r.GetProperty("constructionId").GetGuid());
        Assert.Equal(2, rows.Count);
        Assert.All(rows.Values, r => Assert.Equal("not-tracked", r.GetProperty("contact").GetProperty("state").GetString()));
        var eastCells = rows[east].GetProperty("cells");
        var westCells = rows[west].GetProperty("cells");
        Assert.Equal(SummaryStates.NotAssigned, State(eastCells[0][0]));
        Assert.Equal(SummaryStates.ZeroHours, State(eastCells[1][0]));
        Assert.Equal(report.Id, eastCells[1][0].GetProperty("reportId").GetGuid());
        Assert.Equal(0m, eastCells[1][0].GetProperty("hours").GetDecimal());
        var presence = eastCells[1][1];
        Assert.Equal(SummaryStates.Draft, State(presence));
        Assert.Equal("night", presence.GetProperty("shift").GetString());
        Assert.Equal(draft.WindowEndsAt, presence.GetProperty("autoSubmitAt").GetDateTimeOffset());
        Assert.Equal(new[] { "autoSubmitAt", "shift", "state" }, presence.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(SummaryStates.WindowOpen, State(eastCells[2][0]));
        Assert.Equal(SummaryStates.WindowOpen, State(westCells[1][0]));
        Assert.Equal(SummaryStates.Future, State(westCells[2][0]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Собственные_отчёты_и_охват_менеджера_складываются_без_чужих_строк(bool assigned)
    {
        var directory = await world.Assignments.DirectoryAsync();
        var local = await world.Assignments.SiteAsync("Управляемая стройка");
        var outside = await world.Assignments.SiteAsync("Стройка своего отчёта", "Asia/Vladivostok");
        var foreign = await world.Assignments.SiteAsync("Стройка чужого отчёта");
        var own = await world.Assignments.EmployeeAsync(directory, "Менеджер и монтажник");
        var other = await world.Assignments.EmployeeAsync(directory, "Другой монтажник");
        var (user, client) = await world.Assignments.UserAsync("Менеджер и монтажник", WorkModule.ReportReview, WorkModule.ReportOwn);
        using var manager = client;
        await world.Assignments.LinkAsync(own, user);
        if (assigned) await world.Assignments.AssignedAsync(own, local, AssignmentKind.Manager, "2026-01-01", null);
        foreach (var site in new[] { local, outside, foreign })
            await world.Assignments.AssignedAsync(other, site, AssignmentKind.Installer, "2026-10-31", "2026-11-01");
        await world.Assignments.AssignedAsync(own, outside, AssignmentKind.Installer, "2026-10-31", "2026-11-01");
        await SeedReportsAsync([
            MakeReport(own, local, October31, ReportShift.Day, 7),
            MakeReport(own, outside, October31, ReportShift.Day, 7),
            MakeReport(other, outside, October31, ReportShift.Day, 7),
            MakeReport(other, foreign, October31, ReportShift.Day, 7),
        ]);

        var page = await manager.GetFromJsonAsync<JsonElement>(Url);
        Assert.Equal(assigned ? "own-and-sites" : "own", page.GetProperty("boundaryOutcome").GetString());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("reason").ValueKind);
        var rows = Rows(page).EnumerateArray().ToDictionary(
            r => (r.GetProperty("employeeId").GetGuid(), r.GetProperty("constructionId").GetGuid()));
        Assert.Equal(assigned ? 3 : 2, rows.Count);
        Assert.Contains((own, local), rows.Keys);
        Assert.Equal(assigned, rows.ContainsKey((other, local)));
        Assert.Equal(SummaryStates.WindowOpen, State(rows[(own, outside)].GetProperty("cells")[2][0]));
        var filtered = await manager.GetFromJsonAsync<JsonElement>($"{Url}&construction={outside}");
        var ownRow = Assert.Single(Rows(filtered).EnumerateArray());
        Assert.Equal(own, ownRow.GetProperty("employeeId").GetGuid());
        Assert.Equal(outside, ownRow.GetProperty("constructionId").GetGuid());
        Assert.Equal(1, filtered.GetProperty("total").GetInt32());
        Assert.Equal(Sites(page), Sites(filtered));
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync(
            $"{SummaryEndpoints.Path}?from=2026-11-01&to=2026-11-01&construction={outside}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"{Url}&construction={foreign}")).StatusCode);
    }

    [Theory]
    [InlineData("2026-10-01", "2026-11-01", "Период сводки — не больше 31 дня.")]
    [InlineData("2026-10-02", "2026-10-01", "Начало периода (from) позже его конца (to).")]
    public async Task Неверный_период_возвращает_400_с_причиной(string from, string to, string error)
    {
        using var reader = (await world.Assignments.UserAsync("Руководитель", WorkModule.ReportRead)).Client;
        using var response = await reader.GetAsync($"{SummaryEndpoints.Path}?from={from}&to={to}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, await AssignmentWorld.ErrorAsync(response));
    }

    [Fact]
    public async Task Две_страницы_по_100_строк_полного_месяца_помещаются_в_лимит_ответа()
    {
        var type = await world.Assignments.DirectoryAsync();
        var site = await world.Assignments.SiteAsync("Жилой комплекс Маяк");
        var reports = new List<Report>();
        for (var i = 0; i < 200; i++)
        {
            var employee = await world.Assignments.EmployeeAsync(type, $"Монтажник участка № {i + 1}");
            for (var day = 0; day < 31; day++)
            {
                var date = new DateOnly(2026, 10, day + 1);
                reports.Add(MakeReport(employee, site, date, ReportShift.Day, 8.5m));
                reports.Add(MakeReport(employee, site, date, ReportShift.Night, 10.5m));
            }
        }
        await SeedReportsAsync(reports);
        using var reader = (await world.Assignments.UserAsync("Руководитель", WorkModule.ReportRead)).Client;
        const string month = SummaryEndpoints.Path + "?from=2026-10-01&to=2026-10-31";
        var seen = new HashSet<Guid>();
        foreach (var offset in new[] { 0, 100 })
        {
            var bytes = await reader.GetByteArrayAsync($"{month}&offset={offset}");
            using var document = JsonDocument.Parse(bytes);
            var page = document.RootElement;
            Assert.Equal(200, page.GetProperty("total").GetInt32());
            Assert.Equal(offset, page.GetProperty("offset").GetInt32());
            Assert.Equal(100, page.GetProperty("limit").GetInt32());
            Assert.Equal(100, Rows(page).GetArrayLength());
            Assert.All(Rows(page).EnumerateArray(), row =>
            {
                Assert.True(seen.Add(row.GetProperty("employeeId").GetGuid()));
                Assert.Equal(31, row.GetProperty("cells").GetArrayLength());
            });
            Assert.True(bytes.Length < 1_000_000);
            output.WriteLine($"Сводка 100 × 31, offset={offset}, две смены в каждой клетке: {bytes.Length} байт.");
        }
        Assert.Equal(200, seen.Count);
    }

    private static Report MakeReport(Guid employee, Guid site, DateOnly date, ReportShift shift, decimal hours)
    {
        var endsAt = SubmissionWindow.EndsAt("Europe/Moscow", date, shift);
        var report = Report.Create(Guid.NewGuid(), employee, site, date, shift, hours, null, 1, Now, endsAt);
        report.Submit(Now, ReportSubmitter.Manual, null, 2, Now);
        return report;
    }

    private async Task SeedReportsAsync(IEnumerable<Report> reports)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
        db.AddRange(reports);
        await db.SaveChangesAsync();
    }

    private static JsonElement Rows(JsonElement page) => page.GetProperty("rows");

    private static IEnumerable<Guid> Sites(JsonElement page) =>
        page.GetProperty("constructions").EnumerateArray().Select(site => site.GetProperty("id").GetGuid());

    private static string? State(JsonElement entry) => entry.GetProperty("state").GetString();
}
