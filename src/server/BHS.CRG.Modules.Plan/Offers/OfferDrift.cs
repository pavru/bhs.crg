using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Offers;

internal sealed record OfferRevisionState(
    OfferRevisionDto Source, OfferRevisionRef? Current, OfferRevisionRef Latest);

/// <summary>Пометки D17. Расхождение каждый раз выводится из состояния сметы.</summary>
internal static class OfferDrift
{
    internal static OfferDriftDto Of(OfferRevisionState revisions)
    {
        var (source, current, latest) = revisions;
        var (code, text) = current is not null && current.Id != source.Id
            ? ("current-changed", $"Сделано по ред. {source.Number}, действующая — ред. {current.Number}")
            : latest.Number > source.Number
                ? ("newer-revision", $"Сделано по ред. {source.Number}, в смете есть ред. {latest.Number}")
                : ("none", $"Сделано по действующей ред. {source.Number}");
        return new(code, text, new(source.Id, source.Number), current, latest);
    }

    internal static Task<Dictionary<Guid, OfferRevisionState>> ReadAsync(
        PlanDbContext db, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var wanted = ids.OfType<Guid>().Distinct().ToArray();
        if (wanted.Length == 0) return Task.FromResult(new Dictionary<Guid, OfferRevisionState>());
        return (
                from r in db.EstimateRevisions.AsNoTracking()
                join e in db.Estimates.AsNoTracking() on r.EstimateId equals e.Id
                where wanted.Contains(r.Id)
                select new OfferRevisionState(
                    new OfferRevisionDto(r.Id, r.Number, e.Id, e.Name),
                    db.EstimateRevisions.Where(c => c.Id == e.CurrentRevisionId)
                        .Select(c => new OfferRevisionRef(c.Id, c.Number)).FirstOrDefault(),
                    db.EstimateRevisions.Where(c => c.EstimateId == e.Id).OrderByDescending(c => c.Number)
                        .Select(c => new OfferRevisionRef(c.Id, c.Number)).First()))
            .ToDictionaryAsync(r => r.Source.Id, ct);
    }
}
