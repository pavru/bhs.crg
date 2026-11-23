using System.Data;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Offers;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BHS.CRG.Modules.Plan.Endpoints;

public static partial class OfferEndpoints
{
    private static async Task<IResult> RebasePreviewAsync(
        Guid id, Guid? revisionId, PlanDbContext db, OfferDesk desk, IModuleUser user, CancellationToken ct)
    {
        var cost = await user.HasAsync(PlanModule.EstimateCost, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var offer = await LoadAsync(db, id, ct);
        var prepared = await OfferRebase.PrepareAsync(db, desk, offer, db.VersionOf(offer), revisionId, cost, ct);
        return Results.Ok(prepared.Preview);
    }

    private static async Task<IResult> RebaseAsync(
        Guid id, OfferRebaseRequest body, HttpRequest request, PlanDbContext db, OfferDesk desk,
        OfferLifecycle lifecycle, IModuleUser user, IModuleClock clock, IModuleActivityLog log, CancellationToken ct)
    {
        var cost = await user.HasAsync(PlanModule.EstimateCost, ct);
        PreparedOfferRebase prepared;
        OfferDto? updated;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                (prepared, updated) = await ApplyAsync(id, body, request, db, desk, lifecycle, user, clock, cost, ct);
                break;
            }
            catch (Exception ex) when (ex.GetBaseException() is PostgresException
                { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected })
            {
                if (attempt == 2)
                    throw new ConflictException("КП или смету одновременно изменяют. Повторите предпросмотр.", ex);
                db.ChangeTracker.Clear();
            }
        }

        var preview = prepared.Preview;
        if (updated is null)
            return Results.Conflict(new
            {
                error = "Смета изменилась после предпросмотра. Сверьте новую разницу и подтвердите ещё раз.",
                preview,
            });

        await log.RecordAsync(OfferActions.Rebased, id.ToString(), $"КП № {updated.Number}",
            before: $"редакция {preview.Source.Number}",
            after: $"редакция {preview.Target.Number}; версия КП {updated.CurrentVersion}", ct: ct);
        return Results.Ok(new OfferRebaseResultDto(updated, prepared.Removed.ToList()));
    }

    private static async Task<(PreparedOfferRebase Prepared, OfferDto? Updated)> ApplyAsync(
        Guid id, OfferRebaseRequest body, HttpRequest request, PlanDbContext db, OfferDesk desk,
        OfferLifecycle lifecycle, IModuleUser user, IModuleClock clock, bool cost, CancellationToken ct)
    {
        // Состояние сметы для отпечатка и запись читаются в одной транзакции. При гонке с правкой
        // целевой редакции PostgreSQL прерывает перенос, повтор сверяет подтверждение с новым снимком.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var offer = await LoadAsync(db, id, ct);
        var version = db.VersionOf(offer);
        EstimateEndpoints.EnsureSeen(request, version, "КП", "КП");
        if (string.IsNullOrWhiteSpace(body.Fingerprint))
            throw new InvalidRequestException("Сначала запросите предпросмотр и передайте его «fingerprint».");

        var prepared = await OfferRebase.PrepareAsync(db, desk, offer, version, body.RevisionId, cost, ct,
            fallbackToCurrent: true);
        if ((body.RevisionId is { } requested && requested != prepared.Target.Id) ||
            !string.Equals(prepared.Preview.Fingerprint, body.Fingerprint, StringComparison.Ordinal))
            return (prepared, null);
        if (offer.RevisionId == prepared.Target.Id)
            throw new ConflictException("КП уже сделано из этой редакции. Выберите другую редакцию сметы.");

        var now = clock.UtcNow();
        if (offer.Status != OfferStatus.Draft) lifecycle.BeginNextDraft(offer, now);

        offer.ChangeRevision(prepared.Target.Id, now);
        db.OfferLines.RemoveRange(offer.Lines);
        offer.Lines.Clear();
        offer.Lines.AddRange(prepared.Lines);
        db.OfferLines.AddRange(prepared.Lines);
        OfferPricing.Retotal(offer);
        prepared.Target.Freeze(new EstimateFreeze("offer", offer.Label), now);
        await db.SaveChangesAsync(ct);
        var updated = await OfferViews.OneAsync(db, desk, user, id, ct);
        await transaction.CommitAsync(ct);
        return (prepared, updated);
    }
}
