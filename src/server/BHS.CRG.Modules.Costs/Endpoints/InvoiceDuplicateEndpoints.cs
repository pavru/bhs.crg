using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

public sealed record ConfirmInvoiceDuplicateRequest(Guid OtherInvoiceId, Guid OtherRevision, string? Reason);

internal static class InvoiceDuplicateEndpoints
{
    internal static async Task<Ok<InvoiceView>> ConfirmAsync(
        Guid id, ConfirmInvoiceDuplicateRequest body, CostsDbContext db, InvoiceDesk desk,
        IModuleUser user, IModuleClock clock, InvoiceDuplicateJournal journal, CancellationToken ct)
    {
        var reason = body.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > InvoiceDuplicateConfirmation.ReasonLimit)
            throw new InvalidRequestException(
                $"Укажите, почему это разные счета. Причина — до {InvoiceDuplicateConfirmation.ReasonLimit} знаков.");
        if (body.OtherInvoiceId == Guid.Empty || body.OtherRevision == Guid.Empty)
            throw new InvalidRequestException("Назовите второй счёт и редакцию, показанную при его сверке.");

        var (invoice, confirmation) = await desk.WriteAsync(id, async write =>
        {
            var invoice = write.Invoice;
            var other = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(i => i.Id == body.OtherInvoiceId, ct);
            if (other is null || other.DuplicateRevision != body.OtherRevision || !InvoiceDuplicates.IsPair(invoice, other)
                || invoice.State == InvoiceState.Rejected || other.State == InvoiceState.Rejected
                || !(InvoiceDuplicates.CanBlock(invoice) || InvoiceDuplicates.CanBlock(other)))
                throw new ConflictException("Пара счетов изменилась. Перечитайте оба счёта перед подтверждением.");

            var (first, second) = InvoiceDuplicateConfirmation.Ordered(invoice, other);
            var confirmation = await db.InvoiceDuplicateConfirmations.SingleOrDefaultAsync(saved =>
                saved.FirstInvoiceId == first.Id && saved.FirstRevision == first.DuplicateRevision
                && saved.SecondInvoiceId == second.Id && saved.SecondRevision == second.DuplicateRevision, ct);
            if (confirmation is not null) return (invoice, confirmation);

            confirmation = new InvoiceDuplicateConfirmation
            {
                EventId = Guid.NewGuid(),
                FirstInvoiceId = first.Id,
                FirstRevision = first.DuplicateRevision,
                SecondInvoiceId = second.Id,
                SecondRevision = second.DuplicateRevision,
                FirstLabel = InvoiceEndpoints.Label(first),
                SecondLabel = InvoiceEndpoints.Label(second),
                ConfirmedAt = clock.UtcNow(),
                AuthorId = user.Id?.ToString() ?? "",
                AuthorName = user.Name,
                Reason = reason,
            };
            db.InvoiceDuplicateConfirmations.Add(confirmation);
            await db.SaveChangesAsync(ct);
            return (invoice, confirmation);
        }, ct, WhenLocked.Decide, duplicate: InvoiceDuplicateWrite.Change);

        await journal.DeliverAsync(confirmation, ct);
        return TypedResults.Ok(await desk.ViewAsync(invoice, ct));
    }
}
