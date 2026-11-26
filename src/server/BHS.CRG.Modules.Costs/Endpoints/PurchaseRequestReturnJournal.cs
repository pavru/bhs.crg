using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Modules.Costs.Endpoints;

internal sealed class PurchaseRequestReturnJournal(
    CostsDbContext db, IModuleDeferredActivityLog log, ILogger<PurchaseRequestReturnJournal> logger) : IModuleJobHandler
{
    private const string Code = "costs.purchase-request-return-journal";
    internal static readonly ModuleTick Tick =
        new("purchase-request-return-journal", "Доставка причин возврата заявок в журнал", Code, ModuleTick.MinPeriod);

    public string Operation => Code;

    public async Task RunAsync(ModuleJobRun run, CancellationToken ct)
    {
        var pending = await db.PurchaseRequestReturnEvents.OrderBy(saved => saved.EventId).Take(100).ToListAsync(ct);
        foreach (var saved in pending) await DeliverAsync(saved, ct);
    }

    internal async Task DeliverAsync(PurchaseRequestReturnEvent saved, CancellationToken ct)
    {
        try
        {
            await log.RecordDeferredAsync(PurchaseRequestActions.Returned, new ModuleDeferredActivity(
                saved.EventId, saved.AuthorId, saved.AuthorName, saved.OccurredAt, saved.RequestId.ToString(),
                $"Заявка № {saved.RequestNumber}", saved.Reason), ct);
            // Повтор с тем же ID безопасен, если журнал успел принять событие, а убрать его здесь не удалось.
            db.PurchaseRequestReturnEvents.Remove(saved);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            db.Entry(saved).State = EntityState.Detached;
            logger.LogWarning(error, "Причина возврата {EventId} сохранена, доставку повторит такт модуля.", saved.EventId);
        }
    }
}
