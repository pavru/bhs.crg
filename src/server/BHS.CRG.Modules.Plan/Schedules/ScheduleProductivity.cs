using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Plan.Endpoints;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Plan.Schedules;

internal sealed class ScheduleProductivity(IModuleContracts contracts, IModuleReaders readers)
{
    internal async Task<ScheduleDto> AddAsync(ScheduleDto schedule, CancellationToken ct)
    {
        if (schedule.Works.Count == 0) return schedule;
        var provider = contracts.Get<IWorkProductivity>();
        if (provider is ModuleContractAnswer<IWorkProductivity>.NotConnected missing)
            return Map(schedule, _ => new("not-connected", Text: missing.Text));

        var work = ((ModuleContractAnswer<IWorkProductivity>.Connected)provider).Contract;
        var reader = await readers.ForRequestAsync(ct);
        var answers = await work.ReadManyAsync(reader, schedule.Works.Select(Query).Distinct().ToArray(), ct);
        return Map(schedule, item => answers[Query(item)] switch
        {
            ContractAnswer<WorkProductivityAnswer>.Shown(var shown) => new(shown.State, shown.Months, shown.MinSample),
            ContractAnswer<WorkProductivityAnswer>.Hidden denied => new("hidden", Permission: denied.Permission, Text: denied.Text),
            ContractAnswer<WorkProductivityAnswer>.SystemRefused refused => new("not-available", Text: refused.Text),
            _ => new("not-available"),
        });
    }

    private static WorkProductivityQuery Query(ScheduleWorkDto work) => new(work.WorkTypeId, work.UnitId);

    private static ScheduleDto Map(ScheduleDto schedule, Func<ScheduleWorkDto, ScheduleWorkProductivity> value) =>
        schedule with { Works = [.. schedule.Works.Select(work => work with { Productivity = value(work) })] };
}
