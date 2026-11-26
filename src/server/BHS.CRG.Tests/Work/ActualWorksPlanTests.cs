using BHS.CRG.Api.Modules;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work;
using BHS.CRG.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Work;

public sealed class ActualWorksPlanHost : AssignmentHost
{
    protected override string ModuleCode => "plan,work";
    protected override string DatabaseSuffix => "actualworks";
}

public class ActualWorksPlanTests(ActualWorksPlanHost host) : IClassFixture<ActualWorksPlanHost>
{
    [Fact]
    public async Task Zero_volume_in_a_real_estimate_does_not_exclude_an_accepted_actual_volume()
    {
        var world = new ReviewWorld(host);
        await world.SeedAsync();
        var site = world.SiteA;
        var (_, installer) = await world.Reports.InstallerAsync("Монтажник");
        var (_, manager) = await world.Reports.ManagerAsync("Менеджер", site);
        var id = Guid.NewGuid();
        var submitted = await world.SubmittedAsync(installer, id, site);
        await CrewWorld.JsonAsync(await ReviewWorld.AcceptAsync(manager, id, ReviewWorld.Version(submitted)));

        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;
        await services.ProjectModuleTypesAsync();
        var port = services.GetRequiredService<IModuleContracts>();
        var positions = services.GetRequiredService<IModulePlanItems>();
        var key = new PlanItemKey(world.Reports.WorkType, site, null, world.Reports.Unit);
        var position = (await positions.FindOrCreateAsync(key)).Item!.Id;
        var estimate = Estimate.Create(site, null, "Нулевой план", EstimateKind.Main, DateTimeOffset.UtcNow);
        var db = services.GetRequiredService<PlanDbContext>();
        db.Estimates.Add(estimate);
        await db.SaveChangesAsync();
        await services.GetRequiredService<EstimateRevisionWriter>().AddAsync(estimate.Id, new EstimateRevisionSource("fixture.gsfx"),
        [
            new EstimateRowDraft("Прокладка кабеля", WorkTypeId: key.WorkTypeId, UnitId: key.UnitId,
                PlanItemId: position, Factor: 1m, Volume: 0m, SourceVolume: 0m),
        ]);

        var reader = ModuleReader.Of(Guid.NewGuid(), "Менеджер", [PlanModule.EstimateRead, WorkModule.FactsRead]);
        var plan = Assert.IsType<ModuleContractAnswer<IPlannedWorks>.Connected>(port.Get<IPlannedWorks>()).Contract;
        var planAnswer = Assert.IsType<ContractAnswer<PlannedWorksAnswer>.Shown>(await plan.ReadAsync(reader, new(site))).Value;
        var plans = Assert.IsType<ContractAnswer<PlannedVolumes>.Shown>(planAnswer.Volumes).Value;
        var planned = Assert.Single(plans.Items);
        Assert.Equal(position, planned.PlanItemId);
        Assert.Equal(0m, planned.Volume);

        var work = Assert.IsType<ModuleContractAnswer<IActualWorks>.Connected>(port.Get<IActualWorks>()).Contract;
        var fact = Assert.IsType<ContractAnswer<ActualWorksAnswer>.Shown>(await work.ReadAsync(reader, new(site))).Value;
        var actual = Assert.Single(fact.Items);
        Assert.Equal(position, actual.PlanItemId);
        Assert.Equal(120m, actual.Volume);
        Assert.Equal(0, fact.Diagnostics.WithoutPlanItem);
    }
}
