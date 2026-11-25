using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Documents;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Tests.Common;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BHS.CRG.Tests.Plan;

public class ConstructionProgressTests
{
    private const string EstimateRead = PlanModule.EstimateRead;
    private const string FactRead = ActualWorksProbeModule.FactsRead;

    [Fact]
    public async Task План_и_факт_независимы_а_строки_собираются_по_виду_и_единице()
    {
        await using var host = new ProgressHost("plan,costs", "progress", probeWork: true, probePlan: true);
        await host.InitializeAsync();
        var site = await SeedAsync(host);
        Assert.Equal([EstimateRead, FactRead, CostsModule.TotalsRead, CostsModule.ReportRead], Gate(host));
        Assert.IsType<ActualWorksProbeModule>(host.Services.GetRequiredService<ModuleRegistry>().Find("work"));
        var factContract = host.Services.GetRequiredService<ModuleContractCatalog>().Find(typeof(IActualWorks))!;
        Assert.Equal(typeof(ActualWorksProbe), Assert.Single(factContract.Registrations).Implementation);

        using var reader = await GrantedSignIn.WithPermissionsAsync(host, EstimateRead, FactRead);
        var all = await ReadAsync(reader, site.Id);
        var planOnly = Find(all, "rows", "workTypeId", site.PlanWork);
        Assert.Equal(20m, Volume(planOnly, "estimate"));
        Assert.False(planOnly.GetProperty("cells").TryGetProperty("fact", out _));
        var factOnly = Find(all, "rows", "workTypeId", site.FactWork);
        Assert.Equal(12m, Volume(factOnly, "fact"));
        Assert.False(factOnly.GetProperty("cells").TryGetProperty("estimate", out _));
        Assert.Equal("м", factOnly.GetProperty("unitName").GetString());
        Assert.Contains("3", Find(all, "strips", "code", "fact-pending").GetProperty("text").GetString());
        Assert.Contains("2", Find(all, "strips", "code", "fact-crew-multiple-keys").GetProperty("text").GetString());
        AssertState(Find(all, "columns", "code", "fact"), "partial");
        AssertState(all.GetProperty("costs"), "hidden", CostsModule.TotalsRead);
        Assert.False(all.GetProperty("costs").TryGetProperty("totals", out _));

        var section = await ReadAsync(reader, site.Id, site.Section);
        Assert.Equal(5m, Volume(Find(section, "rows", "workTypeId", site.FactWork), "fact"));
        Assert.Equal(8m, Volume(Find(section, "rows", "workTypeId", site.PlanWork), "estimate"));
        Assert.Equal(all.GetProperty("factDiagnostics").GetRawText(), section.GetProperty("factDiagnostics").GetRawText());
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"{Path(site.Id)}?section={Guid.NewGuid()}")).StatusCode);

        using var factReader = await GrantedSignIn.WithPermissionsAsync(host, FactRead);
        var factResponse = await ReadAsync(factReader, site.Id);
        AssertState(Find(factResponse, "columns", "code", "estimate"), "hidden", EstimateRead);
        Assert.Equal(site.FactWork, Assert.Single(factResponse.GetProperty("rows").EnumerateArray()).GetProperty("workTypeId").GetGuid());

        using var planReader = await GrantedSignIn.WithPermissionsAsync(host, EstimateRead);
        var planResponse = await ReadAsync(planReader, site.Id);
        AssertState(Find(planResponse, "columns", "code", "fact"), "hidden", FactRead);
        Assert.Equal(site.PlanWork, Assert.Single(planResponse.GetProperty("rows").EnumerateArray()).GetProperty("workTypeId").GetGuid());
        Assert.False(planResponse.TryGetProperty("factDiagnostics", out _));
        Assert.DoesNotContain(planResponse.GetProperty("strips").EnumerateArray(), s =>
            s.GetProperty("code").GetString()!.StartsWith("fact-", StringComparison.Ordinal));

        using var unrelated = await GrantedSignIn.WithPermissionsAsync(host, CorePermissions.ConstructionsRead, "costs.invoice.read");
        Assert.Equal(HttpStatusCode.Forbidden, (await unrelated.GetAsync(Path(site.Id))).StatusCode);
        using var anonymous = host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Path(site.Id))).StatusCode);
    }

    [Fact]
    public async Task Выключенный_Work_и_Costs_дают_состояния_без_чисел()
    {
        await using var host = new ProgressHost("plan", "progress_disabled", probeWork: false, probePlan: true);
        await host.InitializeAsync();
        var site = await SeedAsync(host);
        Assert.Equal([EstimateRead], Gate(host));

        using var reader = await GrantedSignIn.WithPermissionsAsync(host, EstimateRead);
        var body = await ReadAsync(reader, site.Id);
        Assert.DoesNotContain(body.GetProperty("columns").EnumerateArray(), c => c.GetProperty("code").GetString() == "fact");
        var fact = Find(body, "strips", "code", "fact");
        AssertState(fact, "not-connected");
        Assert.Equal("Учёт работ не подключён", fact.GetProperty("text").GetString());
        Assert.False(body.TryGetProperty("factDiagnostics", out _));
        AssertState(body.GetProperty("costs"), "not-connected");
        Assert.False(body.GetProperty("costs").TryGetProperty("totals", out _));
    }

    [Fact]
    public async Task Без_вкладов_адрес_отказывает_даже_с_правом_на_стройки()
    {
        await using var host = new ProgressHost("id", "progress_no_provider", probeWork: false, probePlan: false);
        await host.InitializeAsync();
        using var reader = await GrantedSignIn.WithPermissionsAsync(host, CorePermissions.ConstructionsRead);
        Assert.Empty(Gate(host));
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(Path(Guid.NewGuid()))).StatusCode);
    }

    private static IReadOnlyCollection<string> Gate(ProgressHost host)
    {
        var endpoint = Assert.Single(EndpointInventory.Routes(host.Services),
            route => EndpointInventory.Route(route) == "/api/constructions/{id:guid}/progress");
        Assert.True(EndpointInventory.IsGated(endpoint));
        return EndpointInventory.GatingPermissions([endpoint]);
    }

    private static string Path(Guid site) => $"/api/constructions/{site}/progress";

    private static async Task<JsonElement> ReadAsync(HttpClient reader, Guid site, Guid? section = null) =>
        await reader.GetFromJsonAsync<JsonElement>(section is { } id ? $"{Path(site)}?section={id}" : Path(site));

    private static JsonElement Find(JsonElement root, string array, string property, string value) =>
        Assert.Single(root.GetProperty(array).EnumerateArray(), item => item.GetProperty(property).GetString() == value);

    private static JsonElement Find(JsonElement root, string array, string property, Guid value) =>
        Find(root, array, property, value.ToString());

    private static decimal Volume(JsonElement row, string column) =>
        row.GetProperty("cells").GetProperty(column).GetProperty("volume").GetDecimal();

    private static void AssertState(JsonElement element, string state, string? permission = null)
    {
        Assert.Equal(state, element.GetProperty("state").GetString());
        Assert.Equal(permission, element.GetProperty("permission").GetString());
    }

    private sealed record Site(Guid Id, Guid Section, Guid PlanWork, Guid FactWork);

    private static async Task<Site> SeedAsync(ProgressHost host)
    {
        var seed = new WorkTypeSeed(host);
        var (unitType, workType) = await seed.ClassifierAsync();
        var unit = await seed.RecordAsync(unitType.Id, "м");
        var planned = await seed.RecordAsync(workType.Id, "Работа сметы", WorkTypeSeed.WorkTypeData("01", "Работа сметы", unit));
        var accepted = await seed.RecordAsync(workType.Id, "Работа учёта", WorkTypeSeed.WorkTypeData("02", "Работа учёта", unit));
        var site = await seed.SendAsync(new CreateConstructionCommand("Стройка", Guid.NewGuid()));
        var first = await seed.SendAsync(new CreateSectionCommand(site.Id, "Корпус 1"));
        var second = await seed.SendAsync(new CreateSectionCommand(site.Id, "Корпус 2"));
        using var scope = host.Services.CreateScope();
        var items = scope.ServiceProvider.GetRequiredService<IModulePlanItems>();
        var planId = Assert.IsType<PlanItemResult>((await items.FindOrCreateAsync(new(planned.Id, site.Id, null, unit.Id))).Item).Id;
        host.Services.GetRequiredService<ProgressPlanData>().Items[site.Id] = (planId, first.Id);

        if (host.Services.GetService<ActualWorksProbeData>() is { } facts)
        {
            var firstId = Assert.IsType<PlanItemResult>((await items.FindOrCreateAsync(new(accepted.Id, site.Id, first.Id, unit.Id))).Item).Id;
            var secondId = Assert.IsType<PlanItemResult>((await items.FindOrCreateAsync(new(accepted.Id, site.Id, second.Id, unit.Id))).Item).Id;
            var date = new DateOnly(2026, 11, 23);
            facts.Facts[site.Id] = new([new(firstId, date, "day", first.Id, 5), new(secondId, date, "night", second.Id, 7)],
                new(new(site.Id, null, null), 3, 2, [new(CrewFactExclusion.MultipleKeys, 2)], 0, 0));
        }

        return new(site.Id, first.Id, planned.Id, accepted.Id);
    }

    private sealed class ProgressHost(string modules, string suffix, bool probeWork, bool probePlan) : ModuleOnlyHost
    {
        protected override string ModuleCode => modules;
        protected override string DatabaseSuffix => suffix;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                if (probePlan)
                {
                    services.AddSingleton<ProgressPlanData>();
                    services.SwapModuleContract<IProgressContribution, ProgressPlanProbe>(PlanModule.ModuleCode);
                }
                if (!probeWork) return;

                var probe = new ActualWorksProbeModule();
                var existing = (ModuleRegistry)services.Single(s => s.ServiceType == typeof(ModuleRegistry)).ImplementationInstance!;
                var registry = new ModuleRegistry([.. existing.Enabled, probe], [.. existing.Disabled.Where(m => m.Code != probe.Code)]);
                services.RemoveAll<ModuleRegistry>();
                services.RemoveAll<IEnabledModules>();
                services.RemoveAll<PermissionCatalog>();
                services.AddSingleton(registry);
                services.AddSingleton<IEnabledModules>(registry);
                services.AddSingleton(new PermissionCatalog([.. CorePermissions.All, .. registry.Enabled.SelectMany(m => m.Permissions)]));
                probe.RegisterServices(services, new ConfigurationBuilder().Build());
            });
        }
    }

    private sealed class ProgressPlanData
    {
        internal ConcurrentDictionary<Guid, (Guid PlanItem, Guid Section)> Items { get; } = new();
    }

    private sealed class ProgressPlanProbe(ProgressPlanData data) : IProgressContribution
    {
        public IReadOnlyList<string> AnswerPermissions => [EstimateRead];

        public Task<ProgressContribution> ReadAsync(ModuleReader reader, Guid site, Guid? section, CancellationToken ct)
        {
            IReadOnlyList<ProgressItemValue> ReadValues()
            {
                if (!data.Items.TryGetValue(site, out var item) || (section != null && section != item.Section))
                    return [];
                return [new(item.PlanItem, new(Volume: section == null ? 20 : 8))];
            }

            var values = ContractAnswer.Refusal<IReadOnlyList<ProgressItemValue>>(reader, EstimateRead)
                ?? ContractAnswer.Shown(ReadValues());
            return Task.FromResult(new ProgressContribution([new("estimate", "План по смете", ProgressColumnKind.Volume, values)], [], []));
        }
    }
}
