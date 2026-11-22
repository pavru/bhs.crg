using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Modules.Costs.Endpoints;

internal sealed class InvoiceDuplicateJournal(
    CostsDbContext db, IModuleDeferredActivityLog log, IModuleClock clock, ILogger<InvoiceDuplicateJournal> logger)
{
    internal async Task PublishAsync(InvoiceDuplicateConfirmation saved, CancellationToken ct)
    {
        if (saved.JournaledAt is not null) return;
        try
        {
            Guid? author = Guid.TryParse(saved.AuthorId, out var id) ? id : null;
            var target = $"{saved.FirstInvoiceId}/{saved.SecondInvoiceId}";
            var label = $"{saved.FirstLabel} и {saved.SecondLabel}";
            await log.PublishAsync(InvoiceActions.DuplicateConfirmed, new ModuleDeferredActivity(
                saved.EventId, author, saved.AuthorName, saved.ConfirmedAt, target, label, saved.Reason), ct);
            saved.MarkJournaled(clock.UtcNow());
            await db.SaveChangesAsync(ct);
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            db.Entry(saved).State = EntityState.Detached;
            logger.LogWarning(error, "Подтверждение {EventId} сохранено, доставку в журнал повторит служба.", saved.EventId);
        }
    }

    internal async Task PublishPendingAsync(CancellationToken ct)
    {
        var pending = await db.InvoiceDuplicateConfirmations.Where(saved => saved.JournaledAt == null)
            .OrderBy(saved => saved.ConfirmedAt).ThenBy(saved => saved.EventId).Take(100).ToListAsync(ct);
        foreach (var saved in pending) await PublishAsync(saved, ct);
    }
}

internal sealed class InvoiceDuplicateJournalWorker(
    IServiceScopeFactory scopes, ILogger<InvoiceDuplicateJournalWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<InvoiceDuplicateJournal>().PublishPendingAsync(ct);
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(error, "Не удалось прочитать подтверждения дублей для доставки в журнал.");
            }
        }
    }
}
