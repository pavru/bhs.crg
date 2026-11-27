using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;

namespace BHS.CRG.Modules.Work.Labor;

internal sealed record ProductivityDiscarded(int MixedReports, int OtherUnit, int NotAccepted, int WithoutPositiveHours,
    int UnusableCrew, int OutsideSection);

internal sealed record LaborProductivityValues(Guid UnitId, WorkProductivitySample MinSample,
    WorkProductivityValue? Total, IReadOnlyList<WorkProductivityMonth> Months);

internal sealed record ProductivityResult(LaborProductivityValues Productivity, ProductivityDiscarded Discarded);

internal sealed class Productivity(IModuleSettings settings)
{
    internal async Task<ProductivityResult> CalculateAsync(LaborSnapshot snapshot, WorkProductivityQuery query,
        Guid? sectionId, CancellationToken ct) =>
        Calculate(snapshot.Reports, snapshot.Crew, query, sectionId, crossModule: false, await MinimumAsync(ct));

    internal async Task<IReadOnlyDictionary<WorkProductivityQuery, WorkProductivityAnswer>> CalculateMonthlyAsync(
        LaborSnapshot snapshot, IReadOnlyCollection<WorkProductivityQuery> queries, CancellationToken ct)
    {
        var minimum = await MinimumAsync(ct);
        // Отчёт может войти только в выборку вида и единицы первой строки. Остальные строки всё равно
        // проверяются на смешанный состав, а неподходящий участник исключает всю бригаду.
        var reports = snapshot.Reports.Where(r => r.Lines.Count > 0)
            .ToLookup(r => new WorkProductivityQuery(r.Lines[0].WorkTypeId, r.Lines[0].UnitId));
        var crew = snapshot.Crew.ToLookup(c => c.Group.WorkTypeId);
        return queries.ToDictionary(query => query, query =>
        {
            var values = Calculate(reports[query], crew[query.WorkTypeId], query, sectionId: null, crossModule: true, minimum);
            var months = values.Productivity.Months;
            var state = months.Any(month => month.Productivity.State == LaborStates.Ok)
                ? LaborStates.Ok : LaborStates.TooLittleData;
            return new WorkProductivityAnswer(query.UnitId, minimum, state, months);
        });
    }

    private async Task<WorkProductivitySample> MinimumAsync(CancellationToken ct) => new(
        (int)await settings.GetAsync(WorkSettings.ProductivityReports, ct),
        (int)await settings.GetAsync(WorkSettings.ProductivityConstructions, ct));

    private static ProductivityResult Calculate(IEnumerable<LaborReport> reports, IEnumerable<LaborCrew> crew,
        WorkProductivityQuery query, Guid? sectionId, bool crossModule, WorkProductivitySample minimum)
    {
        var candidates = reports.Where(r => r.Lines.Any(l => l.WorkTypeId == query.WorkTypeId
            && (sectionId is null || l.SectionId == sectionId)));
        var mixed = 0;
        var otherUnit = 0;
        var notAccepted = 0;
        var withoutHours = 0;
        var outsideSection = 0;
        var eligible = new Dictionary<Guid, LaborReport>();
        foreach (var report in candidates)
        {
            if (!report.Accepted) notAccepted++;
            else if (report.Lines.Any(l => l.WorkTypeId != query.WorkTypeId)) mixed++;
            else if (report.Lines.Any(l => l.UnitId != query.UnitId)) otherUnit++;
            else if (sectionId is not null && report.Lines.Any(l => l.SectionId != sectionId)) outsideSection++;
            else if (report.Hours is null or <= 0m) withoutHours++;
            else eligible.Add(report.Id, report);
        }

        // Одна группа на вид работы и смену. Если один отчёт не прошёл, брать из группы только
        // подходящие часы означало бы делить целый объём на часть затраченного времени.
        var usableCrew = crew.Where(c => c.Group.WorkTypeId == query.WorkTypeId && c.Publication.Published
            && c.ReportIds.All(eligible.ContainsKey)).ToArray();
        var crewMembers = usableCrew.SelectMany(c => c.ReportIds).ToHashSet();
        var included = eligible.Values.Where(r => !r.Lines.Any(l => l.IsCrew) || crewMembers.Contains(r.Id)).ToArray();
        var crewByMonth = usableCrew.ToLookup(c => MonthOf(c.Group.Date));
        var months = included.GroupBy(r => MonthOf(r.Date)).OrderBy(g => g.Key)
            .Select(g => new WorkProductivityMonth(g.Key, Value(g.ToArray(), crewByMonth[g.Key], minimum, crossModule)))
            .ToArray();
        var total = crossModule ? null : Value(included, usableCrew, minimum, crossModule);
        return new(new(query.UnitId, minimum, total, months),
            new(mixed, otherUnit, notAccepted, withoutHours, eligible.Count - included.Length, outsideSection));
    }

    private static WorkProductivityValue Value(IReadOnlyList<LaborReport> reports, IEnumerable<LaborCrew> crew,
        WorkProductivitySample minimum, bool crossModule)
    {
        var sample = new WorkProductivitySample(reports.Count, reports.Select(r => r.ConstructionId).Distinct().Count());
        if (sample.Reports < minimum.Reports || sample.Constructions < minimum.Constructions
            || (crossModule && reports.Select(r => r.EmployeeId).Distinct().Take(2).Count() < 2))
            return new(LaborStates.TooLittleData, null, sample);

        var volume = reports.Sum(r => r.Lines.Where(l => !l.IsCrew).Sum(l => l.Volume)) + crew.Sum(c => c.Group.Total!.Value);
        return new(LaborStates.Ok, volume / reports.Sum(r => r.Hours!.Value), sample);
    }

    private static DateOnly MonthOf(DateOnly date) => new(date.Year, date.Month, 1);
}
