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
    RevisionCoverage Coverage, bool Outdated);

internal sealed record RevisionDerivativeBlock(
    string State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RevisionDerivative>? Items,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Permission)
{
    internal static RevisionDerivativeBlock Visible(IReadOnlyList<RevisionDerivative> items) => new("ok", items, null);
    internal static RevisionDerivativeBlock Hidden(string permission) => new("hidden", null, permission);
}

/// <summary>Производные редакций для чтения сметы (PLAN-22). В проекции нет колонок с деньгами.</summary>
internal static class RevisionDerivatives
{
    // Право появится в каталоге с первым адресом графиков (plan-18). До plan-19 данных графиков нет.
    private const string ScheduleRead = "plan.schedule.read";

    public static async Task<IResult> GetAsync(Guid id, PlanDbContext db, IModuleUser user, CancellationToken ct)
    {
        var canReadOffers = await user.HasAsync(PlanModule.OfferRead, ct);
        var schedules = await user.HasAsync(ScheduleRead, ct)
            ? RevisionDerivativeBlock.Visible([])
            : RevisionDerivativeBlock.Hidden(ScheduleRead);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var estimate = await db.Estimates.AsNoTracking().Where(e => e.Id == id)
            .Select(e => new { e.Id, e.CurrentRevisionId }).SingleOrDefaultAsync(ct) ?? throw EstimateViews.NotFound();
        var revisions = await db.EstimateRevisions.AsNoTracking().Where(r => r.EstimateId == id)
            .OrderBy(r => r.Number).Select(r => new { r.Id, r.Number }).ToListAsync(ct);
        var offers = canReadOffers
            ? await OffersAsync(db, revisions.Select(r => r.Id).ToArray(), estimate.CurrentRevisionId, ct)
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
                schedules,
            }).ToList(),
        });
    }

    private static async Task<ILookup<Guid, RevisionDerivative>> OffersAsync(
        PlanDbContext db, Guid[] revisionIds, Guid? current, CancellationToken ct)
    {
        var matched = db.EstimateRows.AsNoTracking().Where(EstimateRowCounts.MatchedWorks);
        var offers = await db.Offers.AsNoTracking()
            .Where(o => o.RevisionId != null && revisionIds.Contains(o.RevisionId.Value))
            .OrderByDescending(o => o.Date).ThenByDescending(o => o.Year).ThenByDescending(o => o.Sequence)
            .Select(o => new
            {
                o.RevisionId, o.Id, o.Number, o.CurrentVersion, o.Status, o.Date, o.SentOn,
                Total = matched.Count(r => r.RevisionId == o.RevisionId),
                Covered = matched.Count(r => r.RevisionId == o.RevisionId && o.Lines.Any(l => l.RevisionRowId == r.Id)),
            }).ToListAsync(ct);

        return offers.ToLookup(o => o.RevisionId!.Value,
            o => new RevisionDerivative(o.Id, o.Number, o.CurrentVersion, OfferViews.StatusCode(o.Status), o.Date, o.SentOn,
                RevisionCoverage.Of(o.Covered, o.Total), o.RevisionId != current));
    }
}
