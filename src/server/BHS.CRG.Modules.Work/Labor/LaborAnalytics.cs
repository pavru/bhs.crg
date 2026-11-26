using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Access;

namespace BHS.CRG.Modules.Work.Labor;

internal static class LaborStates
{
    public const string Ok = "ok";
    public const string NotConnected = "not-connected";
    public const string Hidden = "hidden";
    public const string NoAccepted = "no-accepted";
    public const string NoNorms = "no-norms";
    public const string TooLittleData = "too-little-data";
}

internal sealed record SiteLaborPercentage(Guid ConstructionId, Guid? SectionId, LaborPercentage Comparison);
internal sealed record EmployeeLaborPercentage(Guid EmployeeId, string? EmployeeName, LaborPercentage Comparison);

internal sealed record LaborProductivityPage(string State, WorkProductivityAnswer? Productivity = null,
    ProductivityDiscarded? Discarded = null, IReadOnlyList<SiteLaborPercentage>? Sites = null,
    IReadOnlyList<EmployeeLaborPercentage>? Employees = null, string? Permission = null, string? Reason = null);

internal sealed class LaborAnalytics(ManagerSites managerSites, IModuleConstructions constructions, IModuleContracts contracts,
    IModuleEmployees employees, LaborSnapshots snapshots, Productivity productivity)
{
    internal async Task<LaborComparison> CompareAsync(ModuleReader reader, Guid constructionId, Guid? sectionId,
        DateOnly from, DateOnly to, CancellationToken ct)
    {
        var scope = await ScopeAsync(reader, constructionId, sectionId, from, to, ct);
        var (plan, refusal) = OpenPlan(reader, sectionId);
        if (refusal is not null) return refusal;

        var snapshot = await snapshots.ReadAsync(scope, from, to, workTypeId: null, ct);
        var result = await EstimateAsync(plan!, reader, constructionId, snapshot, ct);
        return result.Compare(sectionId);
    }

    internal async Task<LaborProductivityPage> ReadProductivityAsync(ModuleReader reader, Guid? constructionId,
        Guid? sectionId, DateOnly from, DateOnly to, WorkProductivityQuery? query, bool byEmployee,
        CancellationToken ct)
    {
        var scope = await ScopeAsync(reader, constructionId, sectionId, from, to, ct);
        var (plan, refusal) = OpenPlan(reader, sectionId);
        if (refusal is not null) return new(refusal.State, Permission: refusal.Permission, Reason: refusal.Reason);

        var needsEstimate = constructionId is not null || byEmployee || query is null;
        var snapshot = await snapshots.ReadAsync(scope, from, to, needsEstimate ? null : query!.WorkTypeId, ct);
        var rate = query is null ? null : await productivity.CalculateAsync(snapshot, query, sectionId, crossModule: false, ct);
        var siteResults = new List<SiteLaborPercentage>();
        var reportResults = new List<LaborReportEstimate>();
        if (needsEstimate)
        {
            var sites = constructionId is { } site ? [site]
                : snapshot.Reports.Where(r => r.Accepted).Select(r => r.ConstructionId).Distinct().Order().ToArray();
            foreach (var id in sites)
            {
                var estimate = await EstimateAsync(plan!, reader, id, snapshot, ct);
                siteResults.Add(new(id, sectionId, EstimateVsActual.Percentage(estimate.Compare(sectionId))));
                if (byEmployee) reportResults.AddRange(estimate.Reports.Where(r => r.InSection(sectionId)));
            }
        }

        var people = byEmployee ? await EmployeePercentagesAsync(reportResults, sectionId, ct) : null;
        var accepted = snapshot.Reports.Any(r => r.Accepted && (sectionId is null || r.Lines.Any(l => l.SectionId == sectionId)));
        var state = !accepted ? LaborStates.NoAccepted
            : rate?.Productivity.Total.State ?? (siteResults.Any(s => s.Comparison.State == LaborStates.Ok)
                ? LaborStates.Ok : LaborStates.NoNorms);
        return new(state, rate?.Productivity, rate?.Discarded, needsEstimate ? siteResults : null, people);
    }

    private async Task<ModuleReach> ScopeAsync(ModuleReader reader, Guid? constructionId, Guid? sectionId,
        DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (from > to) throw new InvalidRequestException("Начало периода (from) позже его конца (to).");
        if (sectionId is not null && constructionId is null)
            throw new InvalidRequestException("Для отбора по разделу укажите стройку (constructionId).");

        var reach = reader.Allows(WorkModule.ReportRead) ? ModuleReach.All : await managerSites.ReachAsync(reader, ct);
        if (constructionId is not { } id) return reach;
        if (!reach.Covers(id, own: false)) throw new NotFoundException("Стройка не найдена.");
        var construction = (await constructions.ListAsync(ct)).FirstOrDefault(c => c.Id == id)
            ?? throw new NotFoundException("Стройка не найдена.");
        if (sectionId is { } section && !construction.Sections.Any(s => s.Id == section))
            throw new NotFoundException("Раздел не найден.");
        return ModuleReach.OfSites([id]);
    }

    private (IPlannedWorks? Plan, LaborComparison? Refusal) OpenPlan(ModuleReader reader, Guid? sectionId)
    {
        var distributed = sectionId is not null;
        if (contracts.Get<IPlannedWorks>() is not ModuleContractAnswer<IPlannedWorks>.Connected connected)
            return (null, new(LaborStates.NotConnected, distributed, Reason: "Планирование не подключено."));
        if (!reader.Allows(PlannedLabor.Permission) && !reader.Allows(PlannedLabor.EstimateReadPermission))
            return (null, new(LaborStates.Hidden, distributed, Permission: PlannedLabor.Permission,
                Reason: $"Сметные нормы скрыты: нужно право {PlannedLabor.Permission}."));
        return (connected.Contract, null);
    }

    private static async Task<LaborSiteEstimate> EstimateAsync(IPlannedWorks plan, ModuleReader reader,
        Guid site, LaborSnapshot snapshot, CancellationToken ct)
    {
        var ids = snapshot.Reports.Where(r => r.ConstructionId == site && r.Accepted)
            .SelectMany(r => r.Lines).Select(l => l.PlanItemId).Where(snapshot.Positions.ContainsKey).Distinct().ToArray();
        if (ids.Length == 0) return EstimateVsActual.Calculate(site, snapshot, new Dictionary<Guid, decimal?>());

        // Передаём только имеющееся у читателя право на нормы. Остальные права, включая деньги, запросу не нужны.
        var right = reader.Allows(PlannedLabor.Permission) ? PlannedLabor.Permission : PlannedLabor.EstimateReadPermission;
        var laborReader = ModuleReader.Of(reader.UserId!.Value, reader.Who, [right]);
        var answer = await plan.ReadAsync(laborReader, new(site, PlanItemIds: ids), ct);
        if (answer is not ContractAnswer<PlannedWorksAnswer>.Shown
                { Value.Labor: ContractAnswer<IReadOnlyList<PlannedLabor>>.Shown labor })
            throw new InvalidOperationException("Контракт «Плановые работы» не вернул нормы при наличии права на чтение.");

        return EstimateVsActual.Calculate(site, snapshot, labor.Value.ToDictionary(item => item.PlanItemId, item => item.HoursPerUnit));
    }

    private async Task<IReadOnlyList<EmployeeLaborPercentage>> EmployeePercentagesAsync(
        IReadOnlyList<LaborReportEstimate> reports, Guid? sectionId, CancellationToken ct)
    {
        if (reports.Count == 0) return [];
        var byEmployee = reports.GroupBy(r => r.Report.EmployeeId).ToArray();
        var people = await employees.GetAsync([.. byEmployee.Select(g => g.Key)], ct) ?? [];
        var names = people.ToDictionary(p => p.Id, p => p.FullName);
        return byEmployee.OrderBy(g => names.GetValueOrDefault(g.Key), StringComparer.CurrentCulture).ThenBy(g => g.Key)
            .Select(g => new EmployeeLaborPercentage(g.Key, names.GetValueOrDefault(g.Key), LaborSiteEstimate.Employee(g, sectionId)))
            .ToArray();
    }
}
