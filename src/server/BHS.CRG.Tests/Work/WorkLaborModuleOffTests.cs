using System.Net;
using System.Text.Json;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;
using static BHS.CRG.Tests.Work.WorkLaborTests;

namespace BHS.CRG.Tests.Work;

[Collection(AssignmentHost.Collection)]
public class WorkLaborPlanOffTests(AssignmentHost host)
{
    [Fact]
    public async Task Both_http_addresses_report_disabled_plan_without_releasing_actual_hours()
    {
        var world = new WorkLaborWorld(host);
        await world.SeedAsync();
        var site = world.Reports.SiteA;
        var (_, manager) = await world.Reports.ManagerAsync("Менеджер", site);
        var employee = await world.Reports.EmployeeAsync("Монтажник");
        await world.ReportAsync(site, employee, 8, ReportStatus.Accepted, WorkLaborWorld.Day, world.Line(12));

        var comparison = await ReadAsync(manager, WorkLaborWorld.Estimate(site));
        Assert.Equal("not-connected", State(comparison));
        Assert.Equal(JsonValueKind.Null, comparison.GetProperty("actualHours").ValueKind);
        Assert.Equal("not-connected", State(await ReadAsync(manager, world.Productivity())));
        using var scope = host.Services.CreateScope();
        Assert.IsType<ModuleContractAnswer<IWorkProductivity>.Connected>(
            scope.ServiceProvider.GetRequiredService<IModuleContracts>().Get<IWorkProductivity>());
    }
}

[Collection(PlanOnlyCollection.Name)]
public class WorkProductivityWorkOffTests(PlanOnlyHost host)
{
    [Fact]
    public async Task Disabled_work_has_no_provider_and_answers_501_on_both_addresses()
    {
        using var scope = host.Services.CreateScope();
        var answer = Assert.IsType<ModuleContractAnswer<IWorkProductivity>.NotConnected>(
            scope.ServiceProvider.GetRequiredService<IModuleContracts>().Get<IWorkProductivity>());
        Assert.Equal("work", answer.Provider);
        Assert.Equal("Выработка", answer.ContractTitle);

        var client = host.CreateClient();
        foreach (var path in new[] { "estimate-vs-actual", "productivity" })
        {
            var response = await client.GetAsync($"/api/work/labor/{path}");
            Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
            Assert.Equal("work", (await ReportWorld.JsonAsync(response)).GetProperty("module").GetString());
        }
    }
}
