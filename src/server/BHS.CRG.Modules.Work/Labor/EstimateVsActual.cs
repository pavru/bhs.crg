using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Crew;
using BHS.CRG.Modules.Work.Data;

namespace BHS.CRG.Modules.Work.Labor;

internal sealed record OffEstimateWork(Guid WorkTypeId, Guid UnitId, decimal Volume);
internal sealed record OffEstimate(decimal? Hours, IReadOnlyList<OffEstimateWork> Works);
internal sealed record LaborDiagnostics(int ExcludedCrewGroups, int MissingHoursReports, int UnknownCrewShares);
internal sealed record LaborComparison(string State, bool Distributed, decimal? EstimatedHours = null,
    decimal? ActualHours = null, OffEstimate? OffEstimate = null, decimal? UnallocatedHours = null,
    decimal? UnsectionedHours = null, LaborDiagnostics? Diagnostics = null, string? Permission = null, string? Reason = null);

internal sealed record LaborPercentage(string State, bool Distributed, decimal? Percent);

internal static class EstimateVsActual
{
    internal static LaborSiteEstimate Calculate(Guid constructionId, LaborSnapshot snapshot,
        IReadOnlyDictionary<Guid, decimal?> norms)
    {
        var accepted = snapshot.Reports.Where(r => r.Accepted && r.ConstructionId == constructionId)
            .ToDictionary(r => r.Id, r => new LaborReportEstimate(r));
        var volumes = new List<LaborVolumeEstimate>();
        foreach (var report in accepted.Values)
        {
            foreach (var line in report.Report.Lines.Where(l => !l.IsCrew))
            {
                var key = new PlanItemKey(line.WorkTypeId, constructionId, line.SectionId, line.UnitId);
                var norm = CrewPublication.PositionMatches(snapshot.Positions, line.PlanItemId, key)
                    ? norms.GetValueOrDefault(line.PlanItemId) : null;
                var work = new LaborVolumeEstimate(line.SectionId, line.WorkTypeId, line.UnitId, line.Volume,
                    line.Volume * norm);
                volumes.Add(work);
                report.Work.Add(new(work.SectionId, work.Weight, work.Weight is not null));
            }
        }

        foreach (var crew in snapshot.Crew.Where(c => c.Group.ConstructionId == constructionId && c.Publication.Published))
        {
            var members = crew.ReportIds.Where(accepted.ContainsKey).Select(id => accepted[id]).ToArray();
            var key = crew.Publication.Keys.Single();
            var volume = crew.Group.Total!.Value;
            var weight = volume * norms.GetValueOrDefault(key.PlanItemId);
            volumes.Add(new(key.SectionId, crew.Group.WorkTypeId, key.UnitId, volume, weight));
            foreach (var member in members) member.SeenCrew.Add(crew.Group.WorkTypeId);
            AddCrewShares(members, key.SectionId, weight);
        }

        foreach (var report in accepted.Values)
        {
            foreach (var kind in report.Report.Lines.Where(l => l.IsCrew).Select(l => l.WorkTypeId).Distinct())
                if (!report.SeenCrew.Contains(kind)) report.UnusableCrew.Add(kind);
        }

        return new(accepted.Values.ToArray(), volumes);
    }

    private static void AddCrewShares(LaborReportEstimate[] members, Guid? sectionId, decimal? weight)
    {
        var totalHours = members.Sum(m => m.Report.Hours);
        if (weight is null or 0m || totalHours == 0m || members.Any(m => m.Report.Hours is null))
        {
            foreach (var member in members)
                member.Work.Add(new(sectionId, weight is 0m ? 0m : null, weight is not null));
            return;
        }

        foreach (var (member, portion) in Distribute(weight.Value, members.OrderBy(m => m.Report.Id),
                     m => m.Report.Hours!.Value))
            member.Work.Add(new(sectionId, portion, true));
    }

    // Накопительная доля сохраняет сумму точно при округлении decimal, включая последний остаток.
    internal static IEnumerable<(T Item, decimal Portion)> Distribute<T>(decimal amount, IEnumerable<T> items,
        Func<T, decimal> weight)
    {
        var weighted = items.Select(item => (Item: item, Weight: weight(item))).ToArray();
        var total = weighted.Sum(x => x.Weight);
        var cumulative = 0m;
        var distributed = 0m;
        foreach (var (item, value) in weighted)
        {
            cumulative += value;
            var through = amount * (cumulative / total);
            yield return (item, through - distributed);
            distributed = through;
        }
    }

    internal static LaborPercentage Percentage(LaborComparison comparison) =>
        new(comparison.State, comparison.Distributed, comparison.Diagnostics?.ExcludedCrewGroups > 0
            ? null : Percent(comparison.EstimatedHours, comparison.ActualHours));

    internal static decimal? Percent(decimal? estimate, decimal? actual) => actual is > 0m ? estimate / actual * 100m : null;
}

internal sealed record LaborVolumeEstimate(Guid? SectionId, Guid WorkTypeId, Guid UnitId, decimal Volume, decimal? Weight);
internal sealed record LaborShare(Guid? SectionId, decimal? Weight, bool HasNorm);

internal sealed class LaborReportEstimate(LaborReport report)
{
    internal LaborReport Report { get; } = report;
    internal List<LaborShare> Work { get; } = [];
    internal HashSet<Guid> SeenCrew { get; } = [];
    internal HashSet<Guid> UnusableCrew { get; } = [];

    internal bool HasNorm => Work.Any(w => w.HasNorm);
    internal bool UnknownCrewShare => Work.Any(w => w.HasNorm && w.Weight is null);
    internal bool OffEstimate => !HasNorm && UnusableCrew.Count == 0;
    internal bool Unallocated => UnusableCrew.Count > 0
        || (HasNorm && !UnknownCrewShare && Work.Sum(w => w.Weight) <= 0m);

    internal decimal? HoursOn(Guid? sectionId)
    {
        if (OffEstimate || Unallocated) return null;
        return sectionId is null ? Report.Hours : DistributedHoursOn(sectionId);
    }

    internal decimal? DistributedHoursOn(Guid? sectionId)
    {
        if (Report.Hours is not { } hours || UnknownCrewShare) return null;
        var sections = Work.GroupBy(w => w.SectionId)
            .Select(g => (SectionId: g.Key, Weight: g.Sum(w => w.Weight ?? 0m)))
            .Where(s => s.Weight > 0m).OrderBy(s => s.SectionId);
        return EstimateVsActual.Distribute(hours, sections, s => s.Weight)
            .Where(s => s.Item.SectionId == sectionId).Select(s => (decimal?)s.Portion).SingleOrDefault();
    }

    internal bool InSection(Guid? sectionId) => sectionId is null || Report.Lines.Any(l => l.SectionId == sectionId);
}

internal sealed record LaborSiteEstimate(IReadOnlyList<LaborReportEstimate> Reports,
    IReadOnlyList<LaborVolumeEstimate> Volumes)
{
    internal LaborComparison Compare(Guid? sectionId)
    {
        var distributed = sectionId is not null;
        var reports = Reports.Where(r => r.InSection(sectionId)).ToArray();
        if (reports.Length == 0)
            return new(LaborStates.NoAccepted, distributed, Reason: "За выбранный период нет принятых отчётов.");

        var volumes = Volumes.Where(w => sectionId is null || w.SectionId == sectionId).ToArray();
        var normed = volumes.Where(w => w.Weight is not null).ToArray();
        var works = volumes.Where(w => w.Weight is null).GroupBy(w => (w.WorkTypeId, w.UnitId))
            .OrderBy(g => g.Key.WorkTypeId).ThenBy(g => g.Key.UnitId)
            .Select(g => new OffEstimateWork(g.Key.WorkTypeId, g.Key.UnitId, g.Sum(w => w.Volume))).ToArray();
        var comparable = reports.Where(r => !r.OffEstimate && !r.Unallocated).ToArray();
        var actual = distributed ? DistributedHours(comparable, sectionId)
            : SumKnown(comparable.Select(r => r.Report.Hours));

        var offEstimate = new OffEstimate(distributed ? null : SumKnown(reports.Where(r => r.OffEstimate)
            .Select(r => r.Report.Hours)), works);
        var unallocated = distributed ? null : SumKnown(reports.Where(r => r.Unallocated).Select(r => r.Report.Hours));
        var unsectioned = distributed ? null : DistributedHours(comparable, null);
        // Считается каждая негодная группа, затрагивающая хотя бы один отчёт выбранного охвата.
        var excludedGroups = reports.SelectMany(r => r.UnusableCrew.Select(kind => (r.Report.ShiftKey, kind)))
            .Distinct().Count();
        var diagnostics = new LaborDiagnostics(excludedGroups, reports.Count(r => r.Report.Hours is null),
            reports.Count(r => r.UnknownCrewShare));
        return new(normed.Length == 0 ? LaborStates.NoNorms : LaborStates.Ok, distributed,
            normed.Length == 0 ? null : normed.Sum(w => w.Weight), actual, offEstimate, unallocated, unsectioned, diagnostics,
            Reason: normed.Length == 0 ? "Для принятых объёмов нет числовых сметных норм." : null);
    }

    internal static LaborPercentage Employee(IEnumerable<LaborReportEstimate> reports, Guid? sectionId)
    {
        var comparable = reports.Where(r => !r.OffEstimate && !r.Unallocated).ToArray();
        var shares = comparable.SelectMany(r => r.Work).Where(w => sectionId is null || w.SectionId == sectionId)
            .Where(w => w.HasNorm).ToArray();
        if (shares.Length == 0) return new(LaborStates.NoNorms, true, null);
        var actual = SumKnown(comparable.Select(r => r.HoursOn(sectionId)), requireAll: sectionId is null);
        if (reports.Any(r => r.UnusableCrew.Count > 0)
            || comparable.Any(r => r.UnknownCrewShare || r.Report.Hours is null)) actual = null;
        return new(LaborStates.Ok, true, EstimateVsActual.Percent(SumKnown(shares.Select(w => w.Weight)), actual));
    }

    private static decimal? DistributedHours(IReadOnlyList<LaborReportEstimate> reports, Guid? sectionId)
    {
        if (reports.Any(r => (r.UnknownCrewShare || r.Report.Hours is null)
            && r.Work.Any(w => w.SectionId == sectionId && (w.Weight is > 0m || w.HasNorm && w.Weight is null))))
            return null;

        return SumKnown(reports.Select(r => r.DistributedHoursOn(sectionId)), requireAll: false);
    }

    private static decimal? SumKnown(IEnumerable<decimal?> items, bool requireAll = true)
    {
        var values = items.ToArray();
        return values.Length == 0 || (requireAll && values.Any(v => v is null)) || values.All(v => v is null)
            ? null : values.Sum();
    }
}
