using BHS.CRG.Application.Backup;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.DataSets;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Backup;

public partial class BackupService
{
    private static BackupDataSetSource StructuralTableSource(BackupDataSetSource source, HashSet<Guid> systemFileIds)
    {
        if (!systemFileIds.Contains(source.FileId) || !TableSourceMetadata.IsTableMarker(source.SheetOrPath))
            return source;

        return source with
        {
            CachedSchema = TableSourceMetadata.StructuralSchema(source.CachedSchema),
            CachedRowCount = DataSetSource.UnstoredRowCount,
            CachedData = null,
        };
    }

    private async Task RestoreDataSetSourcesAsync(
        BackupDataSetSource[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        if (items.Length == 0) return;
        var existing = await db.DataSetSources.Select(x => x.Id).ToHashSetAsync(ct);
        var files = await db.DataSetFiles.ToDictionaryAsync(x => x.Id, x => x.Format, ct);
        var systemFileIds = files.Where(p => p.Value == DataSetFormat.System).Select(p => p.Key).ToHashSet();

        var (ok, orphans) = Split(items, i => files.ContainsKey(i.FileId));
        Warn(warnings, orphans.Count, "источников данных", "их набора нет ни в копии, ни в системе");

        var (c, u) = await UpsertAsync(ok.Select(i => StructuralTableSource(i, systemFileIds))
            .Select(i => (i.Id, DataSetSource.Restore(
                i.Id, i.FileId, i.Name, i.SheetOrPath, i.ColumnExpressions, i.CachedSchema,
                i.CachedRowCount, i.CachedData, i.Tags, i.RowFilter, i.ComputedColumns, i.SortSpec,
                // Неизвестная причина устаревания — не повод терять источник: это подсказка человеку.
                i.StaleReason is not null && Enum.TryParse<DataSetStaleReason>(i.StaleReason, out var reason)
                    ? reason : null,
                i.MaterializeTypeId, i.MaterializeMapping, i.MaterializeDiscriminator,
                i.MaterializeByIdColumn, i.CreatedAt, i.UpdatedAt))), existing, ct);
        stats.Count("Источники данных", c, u);
    }
}
