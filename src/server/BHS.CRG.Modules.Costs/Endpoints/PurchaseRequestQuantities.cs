using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

internal sealed record PurchaseQuantityAccess(string State, IReadOnlyList<string> AnyOfPermissions)
{
    public static PurchaseQuantityAccess For(bool allowed, params string[] permissions) =>
        allowed ? new("shown", []) : new("hidden", permissions);
}

internal sealed record PurchaseQuantityRow(
    Guid NomenclatureId, string? Unit, decimal OrderedQuantity,
    decimal? InvoicedQuantity, bool? InvoicedUnitMismatch,
    decimal? IssuedForConstructionQuantity, bool? IssuedForConstructionUnitMismatch);

internal sealed record PurchaseRequestQuantities(
    PurchaseQuantityAccess Invoiced, PurchaseQuantityAccess IssuedForConstruction,
    IReadOnlyList<PurchaseQuantityRow> Items)
{
    private const string InvoiceRead = "costs.invoice.read";
    private const string WaybillRead = "costs.waybill.read";

    public static async Task<PurchaseRequestQuantities> ReadAsync(
        CostsDbContext db, IModuleUser user, PurchaseRequest request, CancellationToken ct)
    {
        var canReadInvoices = await user.HasAsync(InvoiceRead, ct);
        var canReadIssued = await user.HasAsync(CostsModule.MaterialsRead, ct) || await user.HasAsync(WaybillRead, ct);
        var materials = request.Lines.Select(line => line.NomenclatureId).Distinct().ToArray();
        var invoiced = canReadInvoices ? await InvoicedAsync(db, request.Id, materials, ct) : [];
        var issued = canReadIssued ? await IssuedAsync(db, request.ConstructionId, ct) : [];
        var invoicesByMaterial = invoiced.ToLookup(line => line.NomenclatureId);
        var issuedByMaterial = issued.ToLookup(line => line.NomenclatureId);

        var rows = new List<PurchaseQuantityRow>();
        foreach (var group in request.Lines.OrderBy(line => line.Position)
            .GroupBy(line => (line.NomenclatureId, Unit: PurchaseRequestLine.ComparableUnit(line.Unit))))
        {
            var (material, unit) = group.Key;
            if (unit is null)
            {
                foreach (var line in group)
                    rows.Add(Row(line.Quantity));
            }
            else rows.Add(Row(group.Sum(line => line.Quantity)));

            PurchaseQuantityRow Row(decimal ordered)
            {
                var (invoiceQuantity, invoiceMismatch) = Compare(canReadInvoices, unit, invoicesByMaterial[material]);
                var (issuedQuantity, issuedMismatch) = Compare(canReadIssued, unit, issuedByMaterial[material]);
                return new(material, unit, ordered, invoiceQuantity, invoiceMismatch, issuedQuantity, issuedMismatch);
            }
        }

        return new(PurchaseQuantityAccess.For(canReadInvoices, InvoiceRead),
            PurchaseQuantityAccess.For(canReadIssued, CostsModule.MaterialsRead, WaybillRead), rows);
    }

    private static Task<List<MeasuredQuantity>> InvoicedAsync(
        CostsDbContext db, Guid requestId, IReadOnlyList<Guid> materials, CancellationToken ct) =>
        (from invoice in db.Invoices.AsNoTracking()
         join line in db.InvoiceLines.AsNoTracking() on invoice.Id equals line.InvoiceId
         where invoice.PurchaseRequestId == requestId
             && line.NomenclatureId != null && materials.Contains(line.NomenclatureId.Value)
         group line by new { line.NomenclatureId, line.Unit } into groupByUnit
         select new MeasuredQuantity(groupByUnit.Key.NomenclatureId!.Value, groupByUnit.Key.Unit,
             groupByUnit.Any(line => line.Quantity == null) ? null : groupByUnit.Sum(line => line.Quantity)))
        .ToListAsync(ct);

    private static async Task<List<MeasuredQuantity>> IssuedAsync(
        CostsDbContext db, Guid constructionId, CancellationToken ct)
    {
        var report = await IssuedMaterials.ReadAsync(db, constructionId, ct);
        return [.. report.Items.Select(line => new MeasuredQuantity(line.NomenclatureId, line.Unit, line.Quantity))];
    }

    private static (decimal? Quantity, bool? UnitMismatch) Compare(
        bool allowed, string? unit, IEnumerable<MeasuredQuantity> source)
    {
        if (!allowed) return (null, null);
        if (unit is null) return (null, true);

        var groups = source.ToLookup(line => PurchaseRequestLine.ComparableUnit(line.Unit));
        var compatible = groups[unit];
        var mismatch = groups.Any(group => group.Key != unit);
        if (compatible.Any(line => line.Quantity is null) || (!compatible.Any() && mismatch))
            return (null, mismatch);
        return (compatible.Sum(line => line.Quantity), mismatch);
    }

    private sealed record MeasuredQuantity(Guid NomenclatureId, string? Unit, decimal? Quantity);
}
