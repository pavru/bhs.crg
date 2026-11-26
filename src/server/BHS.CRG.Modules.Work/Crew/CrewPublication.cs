using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;

namespace BHS.CRG.Modules.Work.Crew;

internal readonly record struct CrewPublicationKey(Guid PlanItemId, Guid? SectionId, Guid UnitId, bool Valid);

internal sealed record CrewPublication(
    bool Published, string? Reason, IReadOnlyList<CrewPublicationKey> Keys, int Accepted, int Participating, int Unsubmitted)
{
    internal static CrewPublication Classify(CrewGroupRecord group, IReadOnlyList<CrewGroupLine> lines,
        IEnumerable<Guid> submitted, IReadOnlyDictionary<Guid, PlanItemKey> positions)
    {
        var keys = lines.Select(l => new CrewPublicationKey(l.PlanItemId, l.SectionId, l.UnitId,
            PositionMatches(positions, l.PlanItemId, new(group.WorkTypeId, group.ConstructionId, l.SectionId, l.UnitId))))
            .Distinct().ToArray();
        var reports = lines.DistinctBy(l => l.ReportId).ToArray();
        var accepted = reports.Count(l => l.Accepted);
        var unsubmitted = lines.SelectMany(l => l.Participants).Except(submitted).Count();
        var exclusion = Exclude(group, keys, accepted, reports.Length);
        return new(reports.Length > 0 && exclusion is null,
            reports.Length == 0 ? "empty" : exclusion is { } reason ? ReasonCode(reason) : null,
            keys, accepted, reports.Length, unsubmitted) { Exclusion = exclusion };
    }

    internal CrewFactExclusion? Exclusion { get; private init; }

    private static CrewFactExclusion? Exclude(CrewGroupRecord group, IReadOnlyList<CrewPublicationKey> keys,
        int accepted, int participating)
    {
        if (participating == 0) return null;
        if (group.State == "pending") return CrewFactExclusion.NotApproved;
        if (group.State == "stale") return CrewFactExclusion.Stale;
        if (accepted != participating) return CrewFactExclusion.AwaitingAcceptance;
        if (group.Total is null || keys.Any(k => !k.Valid || k.UnitId != group.UnitId))
            return CrewFactExclusion.InvalidKey;
        if (keys.Count != 1) return CrewFactExclusion.MultipleKeys;
        return null;
    }

    private static string ReasonCode(CrewFactExclusion reason) => reason switch
    {
        CrewFactExclusion.NotApproved => "not-approved",
        CrewFactExclusion.Stale => "stale",
        CrewFactExclusion.AwaitingAcceptance => "awaiting-acceptance",
        CrewFactExclusion.InvalidKey => "invalid-key",
        CrewFactExclusion.MultipleKeys => "multiple-keys",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    internal static bool PositionMatches(IReadOnlyDictionary<Guid, PlanItemKey> positions, Guid id, PlanItemKey key) =>
        positions.TryGetValue(id, out var saved) && saved == key;

    internal static async Task<IReadOnlyDictionary<Guid, PlanItemKey>> ReadPositionsAsync(
        IModulePlanItems planItems, IEnumerable<Guid> ids, CancellationToken ct) =>
        (await planItems.GetAsync(ids.Distinct().ToArray(), ct)).ToDictionary(p => p.Id, p => p.Key);
}
