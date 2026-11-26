using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Plan.Endpoints;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Plan.Schedules;

internal sealed class ScheduleFacts(IModuleContracts contracts, IModuleReaders readers)
{
    internal async Task<ScheduleDto> AddAsync(ScheduleDto schedule, CancellationToken ct)
    {
        if (schedule.Works.Count == 0) return schedule;

        var provider = contracts.Get<IActualWorks>();
        if (provider is ModuleContractAnswer<IActualWorks>.NotConnected missing)
            return AddState(schedule, new("not-connected", Text: missing.Text));

        var reader = await readers.ForRequestAsync(ct);
        var positions = schedule.Works.Select(item => item.PlanItemId).Distinct().ToArray();
        var work = ((ModuleContractAnswer<IActualWorks>.Connected)provider).Contract;
        var answer = await work.ReadAsync(reader, new(schedule.ConstructionId, PlanItemIds: positions), ct);
        return answer switch
        {
            ContractAnswer<ActualWorksAnswer>.Shown(var facts) => AddVolumes(schedule, facts),
            ContractAnswer<ActualWorksAnswer>.Hidden hidden =>
                AddState(schedule, new("hidden", Permission: hidden.Permission, Text: hidden.Text)),
            _ => throw new InvalidOperationException("Контракт факта отказал в чтении от имени пользователя графика."),
        };
    }

    private static ScheduleDto AddVolumes(ScheduleDto schedule, ActualWorksAnswer facts)
    {
        // Одна выборка на график. null у раздела работы означает «стройка в целом», а не любой раздел.
        var volumes = facts.Items.GroupBy(item => (item.PlanItemId, item.SectionId))
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Volume));
        var state = facts.Diagnostics.IsPartial() ? "partial" : "ok";
        return schedule with
        {
            Works = [.. schedule.Works.Select(work => work with
            {
                Fact = new(state, volumes.TryGetValue((work.PlanItemId, work.SectionId), out var volume) ? volume : null),
            })],
            FactDiagnostics = facts.Diagnostics,
        };
    }

    private static ScheduleDto AddState(ScheduleDto schedule, ScheduleWorkFact fact) =>
        schedule with { Works = [.. schedule.Works.Select(work => work with { Fact = fact })] };
}
