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

    internal static async Task<IReadOnlyList<InvoiceDuplicateBlocker>> ViewsAsync(
        CostsDbContext db, Invoice invoice, IReadOnlyList<InvoiceDuplicateMatch> matches,
        AllocationPlaces known, AllocationPlacesSource places, CancellationToken ct)
    {
        var blockers = Blockers(invoice, matches).ToArray();
        if (blockers.Length == 0) return [];

        var ids = blockers.Select(other => other.Id).ToArray();
        var allocated = await db.InvoiceAllocations.AsNoTracking()
            .Where(part => ids.Contains(part.InvoiceId) && part.ConstructionId != null)
            .Select(part => new { part.InvoiceId, ConstructionId = part.ConstructionId!.Value })
            .Distinct().ToListAsync(ct);
        if (allocated.Count == 0) return blockers.Select(other => View(other, [])).ToArray();

        var sites = ReferenceEquals(known.Sites, AllocationPlaces.None.Sites) ? await places.SitesAsync(ct) : known.Sites;
        var names = sites.ToDictionary(site => site.Id, site => site.Name);
        var byInvoice = allocated.ToLookup(part => part.InvoiceId, part => part.ConstructionId);
        return blockers.Select(other => View(other, [.. byInvoice[other.Id]
            .Select(id => names.GetValueOrDefault(id, "Стройка удалена")).Distinct().Order()])).ToArray();
    }

    private static InvoiceDuplicateBlocker View(Invoice invoice, IReadOnlyList<string> constructionNames) => new(
        invoice.Id, invoice.SupplierId!.Value, invoice.Number!, invoice.IssuedOn!.Value, invoice.Total,
        invoice.Payment == InvoicePaymentState.Paid, invoice.PaidOn, invoice.DuplicateRevision,
        invoice.CreatedAt, invoice.ScanBlobPath, invoice.ScanFileName, constructionNames);
}
