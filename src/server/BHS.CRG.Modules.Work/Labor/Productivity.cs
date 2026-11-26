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
        Guid? sectionId, bool crossModule, CancellationToken ct)
    {
        var minimum = new WorkProductivitySample(
            (int)await settings.GetAsync(WorkSettings.ProductivityReports, ct),
            (int)await settings.GetAsync(WorkSettings.ProductivityConstructions, ct));
        return Calculate(snapshot, query, sectionId, crossModule, minimum);
    }

    private static ProductivityResult Calculate(LaborSnapshot snapshot, WorkProductivityQuery query, Guid? sectionId,
        bool crossModule, WorkProductivitySample minimum)
    {
        var candidates = snapshot.Reports.Where(r => r.Lines.Any(l => l.WorkTypeId == query.WorkTypeId
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
        var usableCrew = snapshot.Crew.Where(c => c.Group.WorkTypeId == query.WorkTypeId && c.Publication.Published
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
