using BHS.CRG.Application.Backup;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Backup;

public partial class BackupService
{
    private async Task RestoreDataSetFilesAsync(
        BackupDataSetFile[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        if (items.Length == 0) return;
        var existing = await db.DataSetFiles.ToDictionaryAsync(f => f.Id, f => f.RecognitionRevision, ct);
        var carriers = await LoadScopeCarriersAsync(ct);

        var (ok, orphans) = Split(items, i =>
            Enum.TryParse<DataSetFormat>(i.Format, out _)
            && Enum.TryParse<CatalogScope>(i.Scope, out var sc)
            && ScopeCarrierExists(sc, i.ScopeId, carriers.Sets, carriers.Sections, carriers.Constructions));
        Warn(warnings, orphans.Count, "наборов данных", "их стройки, раздела или комплекта нет");

        var updated = 0;
        foreach (var item in ok)
        {
            var file = DataSetFile.Restore(
                item.Id, item.Name, Enum.Parse<DataSetFormat>(item.Format), item.BlobPath,
                Enum.Parse<CatalogScope>(item.Scope), item.ScopeId, item.PreprocessingProfile, item.Grouping,
                item.InvoiceRawData, item.RecognitionProfiles, item.CreatedAt, item.UpdatedAt);
            if (existing.TryGetValue(item.Id, out var revision))
            {
                var entry = db.DataSetFiles.Local.FindEntry(item.Id);
                if (entry is not null)
                    entry.CurrentValues.SetValues(file);
                else
                {
                    entry = db.Entry(file);
                    entry.State = EntityState.Modified;
                }
                entry.Property(f => f.RecognitionRevision).OriginalValue = revision;
                updated++;
            }
            else
                db.DataSetFiles.Add(file);
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        stats.Count("Наборы данных", ok.Count - updated, updated);
    }
}
