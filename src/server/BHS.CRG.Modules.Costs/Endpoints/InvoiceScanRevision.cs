using System.Security.Cryptography;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

internal static class InvoiceScanRevision
{
    internal static async Task<(string Path, string Fingerprint)?> ExistingFingerprintAsync(
        CostsDbContext db, IModuleBlobs blobs, Guid id, CancellationToken ct)
    {
        var stored = await db.Invoices.AsNoTracking().Where(i => i.Id == id)
            .Select(i => new { i.ScanBlobPath, i.ScanFingerprint, i.ScanFingerprintSeal }).SingleOrDefaultAsync(ct);
        if (stored?.ScanBlobPath is not { } path ||
            Invoice.Trusted(path, stored.ScanFingerprint, stored.ScanFingerprintSeal) is not null) return null;

        try
        {
            await using var file = await blobs.OpenAsync(path, ct);
            return (path, Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct)));
        }
        catch (NotFoundException)
        {
            // Утраченный файл можно заменить. Равенство байтов при этом доказать нельзя.
            return null;
        }
    }
}
