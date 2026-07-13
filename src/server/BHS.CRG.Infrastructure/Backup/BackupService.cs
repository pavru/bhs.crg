using System.IO.Compression;
using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Domain.Templates;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Backup;

public class BackupService(AppDbContext db, IBlobStorage blob, ILogger<BackupService> logger)
{
    // v2 (issue #84): общие данные теперь DomainObject (без документной фасеты). Старые копии (v1)
    // несовместимы — чистый разрыв (решение пользователя): импорт отклоняется.
    public const int CurrentSchemaVersion = 2;
    public const string CurrentAppVersion = "1.0.0";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // ── Export ────────────────────────────────────────────────────────────────

    public async Task<(Stream ZipStream, string FileName)> ExportAsync(CancellationToken ct = default)
    {
        var manifest = await BuildManifestAsync(ct);

        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Write manifest.json
            var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.Fastest);
            await using (var w = manifestEntry.Open())
                await JsonSerializer.SerializeAsync(w, manifest, JsonOptions, ct);

            // Write binary blobs
            var blobPaths = ExtractBlobPaths(manifest);
            foreach (var blobPath in blobPaths)
            {
                try
                {
                    var blobStream = await blob.DownloadAsync(blobPath, ct);
                    var entry = zip.CreateEntry($"blobs/{blobPath}", CompressionLevel.NoCompression);
                    await using var ew = entry.Open();
                    await blobStream.CopyToAsync(ew, ct);
                }
                catch (Exception ex)
                {
                    // Blob missing in storage — skip, DB reference kept intact
                    logger.LogWarning(ex, "Бинарный файл отсутствует в хранилище при экспорте бэкапа: {BlobPath}", blobPath);
                }
            }
        }

        ms.Position = 0;
        var fileName = $"crg-backup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip";
        return (ms, fileName);
    }

    private async Task<BackupManifest> BuildManifestAsync(CancellationToken ct)
    {
        var docTypes = await db.DocumentTypes.AsNoTracking().ToListAsync(ct);
        var templates = await db.Templates.AsNoTracking().ToListAsync(ct);
        var catalogEntities = await db.CatalogEntities.AsNoTracking().ToListAsync(ct);
        var commonDataEntries = await db.DomainObjects.AsNoTracking().Where(o => o.Facet == null).ToListAsync(ct);
        var primitiveTypes = await db.PrimitiveTypes.AsNoTracking().ToListAsync(ct);

        return new BackupManifest(
            SchemaVersion: CurrentSchemaVersion,
            AppVersion: CurrentAppVersion,
            CreatedAt: DateTimeOffset.UtcNow,
            DocumentTypes: docTypes.Select(dt => new BackupDocumentType(
                dt.Id, dt.Name, dt.Code, dt.Kind.ToString(), dt.ParentId, dt.IsAbstract,
                dt.Schema.RootElement.Clone(), dt.PluginBindings.RootElement.Clone(),
                dt.CreatedAt, dt.UpdatedAt, dt.Group, dt.AllowsProxy)).ToArray(),
            Templates: templates.Select(t => new BackupTemplate(
                t.Id, t.DocumentTypeId, t.Name, t.Content, t.Version,
                t.IsActive, t.IsDefault,
                t.CreatedAt, t.UpdatedAt, t.Parameters)).ToArray(),
            CatalogEntities: catalogEntities.Select(e => new BackupCatalogEntity(
                e.Id, e.EntityType, e.DisplayName, e.Data.RootElement.Clone(), e.OwnerId,
                e.CreatedAt, e.UpdatedAt)).ToArray(),
            CommonDataEntries: commonDataEntries.Select(e => new BackupCommonDataEntry(
                e.Id, e.DisplayName ?? "", e.CompositeTypeId, e.Data.RootElement.Clone(),
                e.ScopeLevel.ToString(), e.ScopeId,
                e.CreatedAt, e.UpdatedAt, e.Aliases.ToArray())).ToArray(),
            PrimitiveTypes: primitiveTypes.Select(p => new BackupPrimitiveType(
                p.Id, p.Name, p.Code, p.BaseType, p.Description,
                p.Constraints.RootElement.Clone(),
                p.CreatedAt, p.UpdatedAt, p.Group)).ToArray());
    }

    // ── Import ────────────────────────────────────────────────────────────────

    public async Task<RestoreReport> ImportAsync(Stream zipStream, CancellationToken ct = default)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

        var manifestEntry = zip.GetEntry("manifest.json")
            ?? throw new InvalidOperationException("Файл не является резервной копией BHS.CRG (отсутствует manifest.json).");

        BackupManifest manifest;
        using (var ms = new MemoryStream())
        {
            await using (var es = manifestEntry.Open())
                await es.CopyToAsync(ms, ct);
            ms.Position = 0;
            manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(ms, JsonOptions, ct)
                       ?? throw new InvalidOperationException("Не удалось прочитать manifest.json.");
        }

        string? conversionNotice = null;
        var warnings = new List<string>();

        if (manifest.SchemaVersion > CurrentSchemaVersion)
            warnings.Add($"Резервная копия создана в более новой версии системы (schema v{manifest.SchemaVersion}). Часть данных могла быть пропущена.");
        else if (manifest.SchemaVersion < CurrentSchemaVersion)
            throw new InvalidOperationException(
                $"Резервная копия создана в старом формате (schema v{manifest.SchemaVersion}) и несовместима с текущей версией " +
                $"после унификации объектов (issue #84). Восстановление такой копии невозможно.");

        // Restore blobs first (before DB, so references are valid on use)
        var blobEntries = zip.Entries.Where(e => e.FullName.StartsWith("blobs/", StringComparison.OrdinalIgnoreCase)).ToList();
        int blobsRestored = 0;
        foreach (var entry in blobEntries)
        {
            var blobPath = entry.FullName["blobs/".Length..];
            if (string.IsNullOrEmpty(blobPath)) continue;
            try
            {
                var contentType = GetContentTypeFromExtension(Path.GetExtension(blobPath));
                using var entryMs = new MemoryStream();
                await using (var es = entry.Open())
                    await es.CopyToAsync(entryMs, ct);
                entryMs.Position = 0;
                await blob.PutAsync(blobPath, entryMs, contentType, ct);
                blobsRestored++;
            }
            catch (Exception ex)
            {
                warnings.Add($"Не удалось восстановить файл «{blobPath}»: {ex.Message}");
            }
        }

        if (blobEntries.Count > 0)
            warnings.Insert(0, $"Файлы: восстановлено {blobsRestored} из {blobEntries.Count}.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var stats = new RestoreStats();
            await RestorePrimitiveTypesAsync(manifest.PrimitiveTypes ?? [], stats, warnings, ct);
            await RestoreDocumentTypesAsync(manifest.DocumentTypes, stats, warnings, ct);
            await RestoreTemplatesAsync(manifest.Templates, stats, warnings, ct);
            await RestoreCatalogEntitiesAsync(manifest.CatalogEntities, stats, warnings, ct);
            await RestoreCommonDataEntriesAsync(manifest.CommonDataEntries, stats, warnings, ct);
            await tx.CommitAsync(ct);

            return new RestoreReport(true, conversionNotice, warnings,
                stats.DocumentTypesCreated, stats.DocumentTypesUpdated,
                stats.TemplatesCreated, stats.TemplatesUpdated,
                stats.CatalogEntitiesCreated, stats.CatalogEntitiesUpdated,
                stats.CommonDataEntriesCreated, stats.CommonDataEntriesUpdated,
                stats.PrimitiveTypesCreated, stats.PrimitiveTypesUpdated);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(CancellationToken.None);
            warnings.Insert(0, $"Ошибка восстановления БД: {ex.Message}");
            return new RestoreReport(false, conversionNotice, warnings, 0, 0, 0, 0, 0, 0, 0, 0);
        }
    }

    // ── Blob path extraction ──────────────────────────────────────────────────

    private static HashSet<string> ExtractBlobPaths(BackupManifest manifest)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in manifest.CommonDataEntries)
            CollectBlobPaths(e.Data, paths);
        foreach (var e in manifest.CatalogEntities)
            CollectBlobPaths(e.Data, paths);
        return paths;
    }

    private static void CollectBlobPaths(JsonElement element, HashSet<string> paths)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("$type", out var typeEl) &&
                    typeEl.GetString() is "file" or "image" &&
                    element.TryGetProperty("blobPath", out var pathEl) &&
                    pathEl.GetString() is { Length: > 0 } path)
                {
                    paths.Add(path);
                }
                else
                {
                    foreach (var prop in element.EnumerateObject())
                        CollectBlobPaths(prop.Value, paths);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectBlobPaths(item, paths);
                break;
        }
    }

    private static string GetContentTypeFromExtension(string ext) =>
        ext.ToLowerInvariant().TrimStart('.') switch
        {
            "pdf"  => "application/pdf",
            "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "xls"  => "application/vnd.ms-excel",
            "png"  => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "gif"  => "image/gif",
            "webp" => "image/webp",
            "svg"  => "image/svg+xml",
            _ => "application/octet-stream",
        };

    // ── Restore helpers ───────────────────────────────────────────────────────

    private async Task RestorePrimitiveTypesAsync(
        BackupPrimitiveType[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        var existingIds = await db.PrimitiveTypes.Select(e => e.Id).ToHashSetAsync(ct);
        foreach (var item in items)
        {
            var entity = PrimitiveType.Restore(
                item.Id, item.Name, item.Code, item.BaseType, item.Description,
                JsonDocument.Parse(item.Constraints.GetRawText()),
                item.CreatedAt, item.UpdatedAt, group: item.Group);
            db.Entry(entity).State = existingIds.Contains(item.Id) ? EntityState.Modified : EntityState.Added;
            if (existingIds.Contains(item.Id)) stats.PrimitiveTypesUpdated++; else stats.PrimitiveTypesCreated++;
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    private async Task RestoreDocumentTypesAsync(
        BackupDocumentType[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        var existingIds = await db.DocumentTypes.Select(e => e.Id).ToHashSetAsync(ct);
        var sorted = TopologicalSortDocTypes(items);
        foreach (var item in sorted)
        {
            if (!Enum.TryParse<DocumentTypeKind>(item.Kind, out var kind))
            {
                warnings.Add($"Тип документа «{item.Name}»: неизвестный вид «{item.Kind}», пропущен.");
                continue;
            }
            var entity = DocumentType.Restore(
                item.Id, item.Name, item.Code, kind, item.ParentId,
                JsonDocument.Parse(item.Schema.GetRawText()),
                JsonDocument.Parse(item.PluginBindings.GetRawText()),
                item.IsAbstract, item.CreatedAt, item.UpdatedAt, item.Group, item.AllowsProxy);
            db.Entry(entity).State = existingIds.Contains(item.Id) ? EntityState.Modified : EntityState.Added;
            if (existingIds.Contains(item.Id)) stats.DocumentTypesUpdated++; else stats.DocumentTypesCreated++;
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    private async Task RestoreTemplatesAsync(
        BackupTemplate[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        var existingIds = await db.Templates.Select(e => e.Id).ToHashSetAsync(ct);
        var validDocTypeIds = await db.DocumentTypes.Select(e => e.Id).ToHashSetAsync(ct);
        foreach (var item in items)
        {
            if (!validDocTypeIds.Contains(item.DocumentTypeId))
            {
                warnings.Add($"Шаблон «{item.Name}» v{item.Version}: тип документа {item.DocumentTypeId} не найден, пропущен.");
                continue;
            }
            var entity = Template.Restore(
                item.Id, item.DocumentTypeId, item.Name, item.Content, item.Version,
                item.IsActive, item.IsDefault,
                item.CreatedAt, item.UpdatedAt, item.Parameters);
            db.Entry(entity).State = existingIds.Contains(item.Id) ? EntityState.Modified : EntityState.Added;
            if (existingIds.Contains(item.Id)) stats.TemplatesUpdated++; else stats.TemplatesCreated++;
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    private async Task RestoreCatalogEntitiesAsync(
        BackupCatalogEntity[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        var existingIds = await db.CatalogEntities.Select(e => e.Id).ToHashSetAsync(ct);
        foreach (var item in items)
        {
            var entity = CatalogEntity.Restore(
                item.Id, item.EntityType, item.DisplayName,
                JsonDocument.Parse(item.Data.GetRawText()),
                item.OwnerId, item.CreatedAt, item.UpdatedAt);
            db.Entry(entity).State = existingIds.Contains(item.Id) ? EntityState.Modified : EntityState.Added;
            if (existingIds.Contains(item.Id)) stats.CatalogEntitiesUpdated++; else stats.CatalogEntitiesCreated++;
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    private async Task RestoreCommonDataEntriesAsync(
        BackupCommonDataEntry[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        // Общие данные восстанавливаем как DomainObject без документной фасеты (issue #84).
        var existingIds = await db.DomainObjects.Select(e => e.Id).ToHashSetAsync(ct);
        var validDocTypeIds = await db.DocumentTypes.Select(e => e.Id).ToHashSetAsync(ct);
        foreach (var item in items)
        {
            if (!validDocTypeIds.Contains(item.CompositeTypeId))
            {
                warnings.Add($"Общие данные «{item.DisplayName}»: тип {item.CompositeTypeId} не найден, пропущен.");
                continue;
            }
            if (!Enum.TryParse<CatalogScope>(item.Scope, out var scope))
            {
                warnings.Add($"Общие данные «{item.DisplayName}»: неизвестная область «{item.Scope}», пропущена.");
                continue;
            }
            var entity = DomainObject.Restore(
                item.Id, item.CompositeTypeId, item.DisplayName,
                JsonDocument.Parse(item.Data.GetRawText()),
                scope, item.ScopeId, item.CreatedAt, item.UpdatedAt, item.Aliases);
            db.Entry(entity).State = existingIds.Contains(item.Id) ? EntityState.Modified : EntityState.Added;
            if (existingIds.Contains(item.Id)) stats.CommonDataEntriesUpdated++; else stats.CommonDataEntriesCreated++;
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    // ── Topological sort ──────────────────────────────────────────────────────

    private static BackupDocumentType[] TopologicalSortDocTypes(BackupDocumentType[] items)
    {
        var result = new List<BackupDocumentType>(items.Length);
        var remaining = items.ToHashSet();
        var addedIds = new HashSet<Guid>();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(x => x.ParentId == null || addedIds.Contains(x.ParentId.Value)).ToList();
            if (ready.Count == 0) { result.AddRange(remaining); break; }
            foreach (var r in ready) { result.Add(r); addedIds.Add(r.Id); remaining.Remove(r); }
        }
        return [.. result];
    }

    private sealed class RestoreStats
    {
        public int PrimitiveTypesCreated, PrimitiveTypesUpdated;
        public int DocumentTypesCreated, DocumentTypesUpdated;
        public int TemplatesCreated, TemplatesUpdated;
        public int CatalogEntitiesCreated, CatalogEntitiesUpdated;
        public int CommonDataEntriesCreated, CommonDataEntriesUpdated;
    }
}
