using BHS.CRG.Modules.Costs.Data;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

internal sealed record InvoiceDuplicateMatch(Invoice Other, bool Confirmed);

internal static class InvoiceDuplicates
{
    internal static bool CanBlock(Invoice invoice) =>
        invoice.State != InvoiceState.Rejected && invoice.Payment != InvoicePaymentState.Paid;

    internal static bool IsPair(Invoice invoice, Invoice other) =>
        invoice.Id != other.Id && invoice.SupplierId is not null && invoice.SupplierId == other.SupplierId
        && !string.IsNullOrEmpty(invoice.Number) && invoice.Number == other.Number
        && invoice.IssuedOn is not null && invoice.IssuedOn == other.IssuedOn;

    internal static async Task<IReadOnlyList<InvoiceDuplicateMatch>> FindAsync(
        CostsDbContext db, Invoice invoice, CancellationToken ct)
    {
        if (invoice.SupplierId is not { } supplier || invoice.Number is not { Length: > 0 } number
            || invoice.IssuedOn is not { } issued) return [];

        var id = invoice.Id;
        var revision = invoice.DuplicateRevision;
        return await db.Invoices.AsNoTracking()
            .Where(other => other.Id != id && other.SupplierId == supplier && other.Number == number && other.IssuedOn == issued)
            .OrderBy(other => other.CreatedAt).ThenBy(other => other.Id)
            .Select(other => new InvoiceDuplicateMatch(other, db.InvoiceDuplicateConfirmations.Any(pair =>
                (pair.FirstInvoiceId == id && pair.FirstRevision == revision
                    && pair.SecondInvoiceId == other.Id && pair.SecondRevision == other.DuplicateRevision)
                || (pair.SecondInvoiceId == id && pair.SecondRevision == revision
                    && pair.FirstInvoiceId == other.Id && pair.FirstRevision == other.DuplicateRevision))))
            .ToListAsync(ct);
    }

    internal static IEnumerable<Invoice> Blockers(Invoice invoice, IReadOnlyList<InvoiceDuplicateMatch> matches) =>
        CanBlock(invoice)
            ? matches.Where(match => !match.Confirmed && match.Other.State != InvoiceState.Rejected).Select(match => match.Other)
            : [];

    internal static async Task<bool> BlocksPaymentAsync(CostsDbContext db, Invoice invoice, CancellationToken ct) =>
        CanBlock(invoice) && Blockers(invoice, await FindAsync(db, invoice, ct)).Any();

    internal static InvoiceDuplicateBlocker View(Invoice invoice) => new(
        invoice.Id, invoice.SupplierId!.Value, invoice.Number!, invoice.IssuedOn!.Value, invoice.Total,
        invoice.Payment == InvoicePaymentState.Paid, invoice.PaidOn, invoice.DuplicateRevision,
        invoice.CreatedAt, invoice.ScanBlobPath, invoice.ScanFileName);
}
