using System.Net;
using System.Text.Json;
using BHS.CRG.Application.Settings;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;
using static BHS.CRG.Tests.Work.WorkLaborTests;

namespace BHS.CRG.Tests.Work;

[Collection(WorkLaborCollection.Name)]
public class WorkProductivityTests(WorkLaborHost host) : IAsyncLifetime
{
    private readonly WorkLaborWorld world = new(host);
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public Task InitializeAsync() => world.SeedAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Contract_checks_monthly_thresholds_without_publishing_the_http_total()
    {
        var employee = await world.Reports.EmployeeAsync("Первый");
        var second = await world.Reports.EmployeeAsync("Второй");
        var oct = new DateOnly(2026, 10, 1);
        var nov = new DateOnly(2026, 11, 1);
        for (var i = 0; i < 5; i++)
        {
            await world.ReportAsync(world.Reports.SiteA, employee, 2, ReportStatus.Accepted, oct.AddDays(i), world.Line(10));
            await world.ReportAsync(world.Reports.SiteB, employee, 2, ReportStatus.Accepted, oct.AddDays(i), world.Line(10));
        }

        using var scope = host.Services.CreateScope();
        var contracts = scope.ServiceProvider.GetRequiredService<IModuleContracts>();
        var provider = Assert.IsType<ModuleContractAnswer<IWorkProductivity>.Connected>(contracts.Get<IWorkProductivity>()).Contract;
        var query = new WorkProductivityQuery(world.Reports.WorkType, world.Reports.Unit);
        var reader = ModuleReader.Of(Guid.NewGuid(), "График", [WorkModule.FactsRead]);
        Assert.Equal(WorkModule.FactsRead, Assert.Single(provider.AnswerPermissions));
        var noAccess = ModuleReader.Of(Guid.NewGuid(), "Без права", [WorkModule.ReportRead]);
        var hidden = Assert.IsType<ContractAnswer<WorkProductivityAnswer>.Hidden>(await provider.ReadAsync(noAccess, query));
        Assert.Equal(WorkModule.FactsRead, hidden.Permission);
        Assert.IsType<ContractAnswer<WorkProductivityAnswer>.SystemRefused>(
            await provider.ReadAsync(ModuleReader.OfSystem("тест контракта"), query));

        var oneWorker = Assert.IsType<ContractAnswer<WorkProductivityAnswer>.Shown>(await provider.ReadAsync(reader, query)).Value;
        var oneWorkerMonth = Assert.Single(oneWorker.Months).Productivity;
        Assert.Equal(new WorkProductivitySample(10, 2), oneWorkerMonth.Sample);
        Assert.Equal("too-little-data", oneWorker.State);
        Assert.Equal("too-little-data", oneWorkerMonth.State);
        Assert.False(JsonSerializer.SerializeToElement(oneWorkerMonth, WebJson).TryGetProperty("volumePerHour", out _));

        await world.ReportAsync(world.Reports.SiteA, second, 2, ReportStatus.Accepted, oct, world.Line(10));
        await world.ReportAsync(world.Reports.SiteB, second, 2, ReportStatus.Accepted, nov, world.Line(10));
        var shown = Assert.IsType<ContractAnswer<WorkProductivityAnswer>.Shown>(await provider.ReadAsync(reader, query)).Value;
        Assert.Equal("ok", shown.State);
        Assert.Collection(shown.Months, first =>
        {
            Assert.Equal(oct, first.Month);
            Assert.Equal(5m, first.Productivity.VolumePerHour);
            Assert.Equal(new WorkProductivitySample(11, 2), first.Productivity.Sample);
        }, secondMonth =>
        {
            Assert.Equal(nov, secondMonth.Month);
            Assert.Equal("too-little-data", secondMonth.Productivity.State);
            Assert.Null(secondMonth.Productivity.VolumePerHour);
            Assert.Equal(new WorkProductivitySample(1, 1), secondMonth.Productivity.Sample);
        });

        var json = JsonSerializer.SerializeToElement(shown, WebJson);
        Keys(json, "unitId", "minSample", "state", "months");
        Keys(json.GetProperty("minSample"), "reports", "constructions");
        var month = json.GetProperty("months")[1];
        Keys(month, "month", "productivity");
        Keys(month.GetProperty("productivity"), "state", "sample");
        Keys(month.GetProperty("productivity").GetProperty("sample"), "reports", "constructions");

        var executive = await AccessProbe.SignInAsync(host, "Executive", "work28");
        var page = await ReadAsync(executive, world.Productivity(oct, nov));
        var total = page.GetProperty("productivity").GetProperty("total");
        Assert.Equal(5m, Number(total, "volumePerHour"));
        Assert.Equal(12m, Number(total.GetProperty("sample"), "reports"));
    }

    [Fact]
    public async Task Http_rate_applies_settings_and_names_mixed_other_unit_unaccepted_and_missing_hours()
    {
        var site = world.Reports.SiteA;
        var (_, manager) = await world.Reports.ManagerAsync("Менеджер", site, PlanModule.LaborRead);
        var person = await world.Reports.EmployeeAsync("Монтажник");
        var other = await new CrewWorld(host).AnotherUnitAsync();
        var date = WorkLaborWorld.Day;
        await world.ReportAsync(site, person, 2, ReportStatus.Accepted, date, world.Line(10));
        await world.ReportAsync(site, person, 2, ReportStatus.Accepted, date.AddDays(1),
            world.Line(10), world.Line(1, type: world.Reports.WorkTypeNeedsPlace));
        await world.ReportAsync(site, person, 2, ReportStatus.Accepted, date.AddDays(2), world.Line(10, unit: other));
        await world.ReportAsync(site, person, 2, ReportStatus.Submitted, date.AddDays(3), world.Line(10));
        await world.ReportAsync(site, person, 2, ReportStatus.Draft, date.AddDays(4), world.Line(10));
        await world.ReportAsync(site, person, null, ReportStatus.Accepted, date.AddDays(5), world.Line(10));
        var url = world.Productivity(date, date.AddDays(5));
        var result = await ReadAsync(manager, url);
        Assert.Equal("too-little-data", State(result));
        var discarded = result.GetProperty("discarded");
        Assert.Equal(1m, Number(discarded, "mixedReports"));
        Assert.Equal(1m, Number(discarded, "otherUnit"));
        Assert.Equal(1m, Number(discarded, "notAccepted"));
        Assert.Equal(1m, Number(discarded, "withoutPositiveHours"));
        Assert.False(result.GetProperty("productivity").GetProperty("total").TryGetProperty("volumePerHour", out _));

        await SetMinimumAsync(1, 1);
        var configured = await ReadAsync(manager, url);
        Assert.Equal("ok", State(configured));
        Assert.Equal(5m, Number(configured.GetProperty("productivity").GetProperty("total"), "volumePerHour"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Crew_contributes_one_approved_volume_and_only_if_all_members_qualify(bool mixed)
    {
        var site = world.Reports.SiteA;
        var (_, manager) = await world.Reports.ManagerAsync("Менеджер", site, PlanModule.LaborRead, WorkModule.CrewApprove);
        await world.NormAsync(site, null, .2m);
        await SetMinimumAsync(1, 1);

        var reports = new List<(Guid Employee, Guid Report, JsonElement Submitted)>();
        foreach (var hours in new[] { 2m, 3m, 5m })
        {
            var (employee, installer) = await world.Reports.InstallerAsync($"Участник {hours}");
            var id = Guid.NewGuid();
            var lines = new List<object>
            {
                new { id = Guid.NewGuid(), workTypeId = world.Reports.WorkType, volume = 40, unitId = world.Reports.Unit,
                    isCrew = true, crewRole = "lead" },
            };
            if (mixed && hours == 5m)
                lines.Add(new { id = Guid.NewGuid(), workTypeId = world.Reports.WorkTypeNeedsPlace,
                    volume = 1, unitId = world.Reports.Unit, location = "Корпус" });
            var draft = await CrewWorld.CreateAsync(installer, new
                { id, constructionId = site, date = ReportWorld.Day16, shift = "day", hours, lines });
            reports.Add((employee, id, await CrewWorld.SubmitAsync(installer, id, draft)));
        }

        var groups = await ReadAsync(manager, $"{CrewWorld.Path}?constructionId={site}&from=2026-11-16&to=2026-11-16");
        var group = Assert.Single(groups.GetProperty("items").EnumerateArray());
        await CrewWorld.JsonAsync(await CrewWorld.ApproveAsync(manager, group.GetProperty("id").GetGuid(), ReviewWorld.Version(group), 100));
        foreach (var (_, report, submitted) in reports.Take(2))
            await CrewWorld.JsonAsync(await ReviewWorld.AcceptAsync(manager, report, ReviewWorld.Version(submitted)));
        var partial = await ReadAsync(manager, WorkLaborWorld.Estimate(site));
        Assert.Equal(JsonValueKind.Null, partial.GetProperty("estimatedHours").ValueKind);
        Assert.Equal(1m, Number(partial.GetProperty("diagnostics"), "excludedCrewGroups"));

        var last = reports.Last();
        await CrewWorld.JsonAsync(await ReviewWorld.AcceptAsync(manager, last.Report, ReviewWorld.Version(last.Submitted)));
        var comparison = await ReadAsync(manager, WorkLaborWorld.Estimate(site));
        Assert.Equal(20m, Number(comparison, "estimatedHours"));
        Assert.Equal(10m, Number(comparison, "actualHours"));
        var result = await ReadAsync(manager, world.Productivity() + "&byEmployee=true");
        var employees = result.GetProperty("employees").EnumerateArray().ToArray();
        Assert.Equal(3, employees.Length);
        Assert.All(employees, e => Assert.Equal(200m, Number(e.GetProperty("comparison"), "percent")));

        var rate = result.GetProperty("productivity").GetProperty("total");
        if (mixed)
        {
            Assert.Equal("too-little-data", State(rate));
            Assert.Equal(1m, Number(result.GetProperty("discarded"), "mixedReports"));
            Assert.Equal(2m, Number(result.GetProperty("discarded"), "unusableCrew"));
        }
        else
        {
            Assert.Equal(10m, Number(rate, "volumePerHour"));
            Assert.Equal(3m, Number(rate.GetProperty("sample"), "reports"));
        }
    }

    private async Task SetMinimumAsync(int reports, int constructions)
    {
        using var scope = host.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsStore>();
        await settings.SetAsync(WorkSettings.ProductivityReports.Key, reports.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await settings.SetAsync(WorkSettings.ProductivityConstructions.Key, constructions.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void Keys(JsonElement json, params string[] expected) =>
        Assert.Equal(expected.Order(), json.EnumerateObject().Select(p => p.Name).Order());
}
