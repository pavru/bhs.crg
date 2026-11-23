using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Plan.Offers;

/// <summary>Отправка, итог и новая версия КП (PLAN-12). История и следующий черновик пишутся одной фиксацией.</summary>
public sealed class OfferLifecycle(PlanDbContext db, IModuleUser user, IModuleClock clock)
{
    public async Task SendAsync(Offer offer, DateOnly date, CancellationToken ct)
    {
        offer.Send(date, clock.UtcNow());
        await db.SaveChangesAsync(ct);
    }

    public async Task OutcomeAsync(Offer offer, OfferStatus status, CancellationToken ct)
    {
        offer.SetOutcome(status, clock.UtcNow());
        await db.SaveChangesAsync(ct);
    }

    public async Task NewVersionAsync(Offer offer, CancellationToken ct)
    {
        BeginNextDraft(offer, clock.UtcNow());
        await db.SaveChangesAsync(ct);
    }

    internal void BeginNextDraft(Offer offer, DateTimeOffset now)
    {
        offer.EnsureSent();
        db.OfferVersions.Add(OfferVersion.Capture(offer, user.Id, user.Name));
        offer.BeginNextVersion(now);
    }
}
