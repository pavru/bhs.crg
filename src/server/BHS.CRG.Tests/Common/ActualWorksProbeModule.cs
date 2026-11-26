using System.Collections.Concurrent;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Modules;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BHS.CRG.Tests.Common;

/// <summary>Поставщик синтетического факта для отдельного тестового состава (STG-6.1).</summary>
internal sealed class ActualWorksProbeModule : IAppModule
{
    internal const string ModuleCode = "work";
    internal const string FactsRead = "work.facts.read";

    public string Code => ModuleCode;
    public string Title => "Учёт работ";
    public IReadOnlyList<string> RoutePrefixes => [];
    public IReadOnlyList<AppPermission> Permissions =>
        [new(FactsRead, "читать факт", "принятые объёмы пробного модуля", ReadAll: ReadAllMark.Out("право контракта"), Contract: true)];

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ActualWorksProbeData>();
        services.AddModuleContract<IActualWorks, ActualWorksProbe>(Code);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) { }

    public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;

    internal static void Enable(IServiceCollection services)
    {
        var existing = (ModuleRegistry)services.Single(s => s.ServiceType == typeof(ModuleRegistry)).ImplementationInstance!;
        var probe = new ActualWorksProbeModule();
        var registry = new ModuleRegistry([.. existing.Enabled, probe], [.. existing.Disabled.Where(m => m.Code != probe.Code)]);
        services.RemoveAll<ModuleRegistry>();
        services.RemoveAll<IEnabledModules>();
        services.RemoveAll<PermissionCatalog>();
        services.AddSingleton(registry);
        services.AddSingleton<IEnabledModules>(registry);
        services.AddSingleton(new PermissionCatalog([.. CorePermissions.All, .. registry.Enabled.SelectMany(m => m.Permissions)]));
        probe.RegisterServices(services, new ConfigurationBuilder().Build());
    }
}

internal sealed class ActualWorksProbeData
{
    internal ConcurrentDictionary<Guid, ActualWorksAnswer> Facts { get; } = new();
}

internal sealed class ActualWorksProbe(ActualWorksProbeData data) : IActualWorks
{
    public IReadOnlyList<string> AnswerPermissions => [ActualWorksProbeModule.FactsRead];

    public Task<ContractAnswer<ActualWorksAnswer>> ReadAsync(
        ModuleReader reader, ActualWorksQuery query, CancellationToken ct = default)
    {
        if (ContractAnswer.Refusal<ActualWorksAnswer>(reader, [.. AnswerPermissions]) is { } refusal)
            return Task.FromResult(refusal);

        var scope = new ActualWorksScope(query.ConstructionId, query.From, query.To);
        var seeded = data.Facts.GetValueOrDefault(query.ConstructionId)
            ?? new ActualWorksAnswer([], new(scope, 0, 0, [], 0, 0));
        var rows = seeded.Items.Where(row =>
            (query.SectionId is null || query.SectionId == row.SectionId) &&
            (query.PlanItemIds is null || query.PlanItemIds.Contains(row.PlanItemId)) &&
            (query.From is null || query.From <= row.Date) && (query.To is null || query.To >= row.Date));
        return Task.FromResult(ContractAnswer.Shown(seeded with { Items = rows.ToArray(), Diagnostics = seeded.Diagnostics with { Scope = scope } }));
    }
}
