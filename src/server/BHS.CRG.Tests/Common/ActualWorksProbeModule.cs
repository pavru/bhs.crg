using System.Collections.Concurrent;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Exchange;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
