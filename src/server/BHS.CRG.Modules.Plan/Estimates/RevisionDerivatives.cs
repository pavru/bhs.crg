using System.Data;
using System.Text.Json.Serialization;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Endpoints;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Estimates;

internal sealed record RevisionCoverage(int Covered, int Total, decimal? Percent)
{
    internal static RevisionCoverage Of(int covered, int total) =>
        new(covered, total, total == 0 ? null : decimal.Round(covered * 100m / total, 2));
}

internal sealed record RevisionDerivative(
    Guid Id, string Number, int CurrentVersion, string Status, DateOnly Date, DateOnly? SentOn,
    RevisionCoverage Coverage, bool Outdated, bool Historical);

internal sealed record RevisionDerivativeBlock(
    string State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RevisionDerivative>? Items,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Permission)
{
    internal static RevisionDerivativeBlock Visible(IReadOnlyList<RevisionDerivative> items) => new("ok", items, null);
    internal static RevisionDerivativeBlock Hidden(string permission) => new("hidden", null, permission);
}

/// <summary>Производные редакций для чтения сметы (PLAN-22). Ответ не содержит денег.</summary>
internal static class RevisionDerivatives
{
    public static async Task<IResult> GetAsync(Guid id, PlanDbContext db, IModuleUser user, IModuleClock clock, CancellationToken ct)
    {
        var canReadOffers = await user.HasAsync(PlanModule.OfferRead, ct);
        var canReadSchedules = await user.HasAsync(PlanModule.ScheduleRead, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var estimate = await db.Estimates.AsNoTracking().Where(e => e.Id == id)
            .Select(e => new { e.Id, e.ConstructionId, e.CurrentRevisionId }).SingleOrDefaultAsync(ct)
            ?? throw EstimateViews.NotFound();
        var revisions = await db.EstimateRevisions.AsNoTracking().Where(r => r.EstimateId == id)
            .OrderBy(r => r.Number).Select(r => new { r.Id, r.Number }).ToListAsync(ct);
        var revisionIds = revisions.Select(r => r.Id).ToArray();
        var offers = canReadOffers ? await OffersAsync(db, revisionIds, estimate.CurrentRevisionId, ct) : null;
        var schedules = canReadSchedules
            ? await SchedulesAsync(db, clock, revisionIds, estimate.ConstructionId, estimate.CurrentRevisionId, ct)
            : null;

        return Results.Ok(new
        {
            estimateId = estimate.Id,
            revisions = revisions.Select(r => new
            {
                revisionId = r.Id,
                r.Number,
                offers = offers is null
                    ? RevisionDerivativeBlock.Hidden(PlanModule.OfferRead)
                    : RevisionDerivativeBlock.Visible(offers[r.Id].ToList()),
                schedules = schedules is null
                    ? RevisionDerivativeBlock.Hidden(PlanModule.ScheduleRead)
                    : RevisionDerivativeBlock.Visible(schedules[r.Id].ToList()),
            }).ToList(),
        });
    }

    private static async Task<ILookup<Guid, RevisionDerivative>> SchedulesAsync(
        PlanDbContext db, IModuleClock clock, Guid[] revisionIds, Guid constructionId, Guid? current, CancellationToken ct)
    {
        var matched = db.EstimateRows.AsNoTracking().Where(EstimateRowCounts.MatchedWorks);
        var schedules = await db.Schedules.AsNoTracking()
            .Where(s => s.RevisionId != null && revisionIds.Contains(s.RevisionId.Value))
            .OrderByDescending(s => s.CreatedAt).ThenBy(s => s.Id)
            .Select(s => new
            {
                s.Id, s.Name, s.CreatedAt, s.RevisionId,
                Total = matched.Count(r => r.RevisionId == s.RevisionId),
                Covered = matched.Count(r => r.RevisionId == s.RevisionId &&
                    db.ScheduleWorks.Any(w => w.ScheduleId == s.Id && w.SourceRowId == r.Id)),
            }).ToListAsync(ct);
        // Поля блока Plan-16 используются существующим экраном. У графика один текущий экземпляр.
        var displayed = new List<(Guid RevisionId, RevisionDerivative Item)>(schedules.Count);
        foreach (var schedule in schedules)
        {
            var date = await clock.SiteDateOfAsync(constructionId, schedule.CreatedAt, ct)
                       ?? DateOnly.FromDateTime(schedule.CreatedAt.UtcDateTime);
            displayed.Add((schedule.RevisionId!.Value, new RevisionDerivative(
                schedule.Id, schedule.Name, CurrentVersion: 1, "Построен", date, SentOn: null,
                RevisionCoverage.Of(schedule.Covered, schedule.Total), schedule.RevisionId != current, Historical: false)));
        }
        return displayed.ToLookup(s => s.RevisionId, s => s.Item);
    }

    private static async Task<ILookup<Guid, RevisionDerivative>> OffersAsync(
        PlanDbContext db, Guid[] revisionIds, Guid? current, CancellationToken ct)
    {
        var matched = db.EstimateRows.AsNoTracking().Where(EstimateRowCounts.MatchedWorks);
        var offers = await db.Offers.AsNoTracking()
            .Where(o => o.RevisionId != null && revisionIds.Contains(o.RevisionId.Value))
            .Select(o => new
            {
                o.RevisionId, o.Id, o.Number, o.Year, o.Sequence, o.CurrentVersion, o.Status, o.Date, o.SentOn,
                Total = matched.Count(r => r.RevisionId == o.RevisionId),
                Covered = matched.Count(r => r.RevisionId == o.RevisionId && o.Lines.Any(l => l.RevisionRowId == r.Id)),
            }).ToListAsync(ct);

        var displayed = offers.Select(o => (Revision: o.RevisionId!.Value, o.Year, o.Sequence,
            Item: new RevisionDerivative(o.Id, o.Number, o.CurrentVersion, OfferViews.StatusCode(o.Status), o.Date, o.SentOn,
                RevisionCoverage.Of(o.Covered, o.Total), o.RevisionId != current, Historical: false))).ToList();

        var history = await OfferHistory.ForRevisionsAsync(db, revisionIds, ct);
        if (history.Count > 0)
        {
            var sources = history.Select(o => o.RevisionId).Distinct().ToArray();
            var rows = await matched.Where(r => sources.Contains(r.RevisionId))
                .Select(r => new { r.RevisionId, r.Id }).ToListAsync(ct);
            var byRevision = rows.ToLookup(r => r.RevisionId, r => r.Id);
            foreach (var old in history)
            {
                var eligible = byRevision[old.RevisionId];
                var coverage = RevisionCoverage.Of(eligible.Count(old.RevisionRows.Contains), eligible.Count());
                displayed.Add((old.RevisionId, old.Year, old.Sequence,
                    new(old.Id, old.Number, old.Version, old.Status, old.Date, old.SentOn,
                        coverage, old.RevisionId != current, Historical: true)));
            }
        }

        return displayed.OrderByDescending(o => o.Item.Date).ThenByDescending(o => o.Year)
            .ThenByDescending(o => o.Sequence).ThenByDescending(o => o.Item.CurrentVersion)
            .ToLookup(o => o.Revision, o => o.Item);
    }
}
