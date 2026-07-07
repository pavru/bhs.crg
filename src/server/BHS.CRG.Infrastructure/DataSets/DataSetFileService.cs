using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Notifications;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Файлы наборов данных: загрузка/замена/скачивание/удаление + детект источников парсером.
/// Часть декомпозиции <see cref="DataSetService"/> (см. архитектурный отчёт, «Предложение 3»).
/// </summary>
public class DataSetFileService(
    AppDbContext db,
    IBlobStorage blob,
    DataSetParserFactory parserFactory,
    ILogger<DataSetFileService> logger,
    INotificationService notifications)
{
    public async Task<IReadOnlyList<DataSetFileDto>> ListFilesAsync(string? scope, Guid? scopeId, CancellationToken ct)
    {
        var q = db.DataSetFiles.Include(f => f.Sources).AsNoTracking().AsQueryable();
        if (scope != null && Enum.TryParse<CatalogScope>(scope, out var s))
            q = q.Where(f => f.Scope == s && f.ScopeId == scopeId);

        var files = await q.OrderBy(f => f.Name).ToListAsync(ct);
        return files.Select(DataSetDtoMapper.MapFile).ToList();
    }

    public async Task<IReadOnlyList<DataSetFileDto>> ListAvailableFilesAsync(Guid setId, CancellationToken ct)
    {
        var set = await db.Set<DocumentSet>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == setId, ct)
            ?? throw new KeyNotFoundException("DocumentSet не найден");
        var section = await db.Set<Section>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == set.SectionId, ct);

        var files = await db.DataSetFiles
            .Include(f => f.Sources)
            .AsNoTracking()
            .Where(f =>
                (f.Scope == CatalogScope.System && f.ScopeId == null) ||
                (f.Scope == CatalogScope.Set && f.ScopeId == setId) ||
                (section != null && f.Scope == CatalogScope.Section && f.ScopeId == section.Id) ||
                (section != null && f.Scope == CatalogScope.Construction && f.ScopeId == section.ConstructionId))
            .OrderBy(f => f.Scope).ThenBy(f => f.Name)
            .ToListAsync(ct);

        return files.Select(DataSetDtoMapper.MapFile).ToList();
    }

    public async Task<DataSetFileDto> UploadFileAsync(UploadFileInput input, CancellationToken ct)
    {
        if (!Enum.TryParse<CatalogScope>(input.Scope, out var scope))
            throw new ArgumentException("Неверный scope");

        var format = DataSetDtoMapper.DetectFormat(input.FileName)
            ?? throw new ArgumentException("Неподдерживаемый формат файла");

        Guid? scopeId = scope != CatalogScope.System && Guid.TryParse(input.ScopeId, out var sid) ? sid : null;
        var name = string.IsNullOrWhiteSpace(input.Name) ? Path.GetFileNameWithoutExtension(input.FileName) : input.Name;

        await using var uploadStream = new MemoryStream(input.Bytes);
        var blobPath = await blob.UploadAsync(input.FileName, uploadStream, input.ContentType ?? "application/octet-stream", ct);

        var parser = parserFactory.GetParser(format);
        var sourceInfos = await parser.DetectSourcesAsync(input.Bytes, ct);

        var dataSetFile = DataSetFile.Create(name, format, blobPath, scope, scopeId);
        foreach (var info in sourceInfos)
            dataSetFile.AddSource(info.Name, info.SheetOrPath, DataSetDtoMapper.SerializeSchema(info.Columns), info.RowCount);

        db.DataSetFiles.Add(dataSetFile);
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapFile(dataSetFile);
    }

    public async Task<DataSetFileDto?> ReplaceFileAsync(Guid id, ReplaceFileInput input, CancellationToken ct)
    {
        var file = await db.DataSetFiles.Include(f => f.Sources).FirstOrDefaultAsync(f => f.Id == id, ct);
        if (file == null) return null;

        var format = DataSetDtoMapper.DetectFormat(input.FileName)
            ?? throw new ArgumentException("Неподдерживаемый формат файла");

        try { await blob.DeleteAsync(file.BlobPath, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "Не удалось удалить старый blob при замене файла {FileId}", id); }

        await using var uploadStream = new MemoryStream(input.Bytes);
        var newBlobPath = await blob.UploadAsync(input.FileName, uploadStream, input.ContentType ?? "application/octet-stream", ct);

        var parser = parserFactory.GetParser(format);
        var sourceInfos = await parser.DetectSourcesAsync(input.Bytes, ct);

        // Match existing sources by sheetOrPath (then name) to preserve bindings.
        var updatedSourceIds = new HashSet<Guid>();
        foreach (var info in sourceInfos)
        {
            var existing = file.Sources.FirstOrDefault(s => s.SheetOrPath == info.SheetOrPath)
                ?? file.Sources.FirstOrDefault(s => s.Name == info.Name);
            if (existing != null)
            {
                existing.UpdateCache(DataSetDtoMapper.SerializeSchema(info.Columns), info.RowCount);
                updatedSourceIds.Add(existing.Id);
            }
            else
            {
                var added = file.AddSource(info.Name, info.SheetOrPath, DataSetDtoMapper.SerializeSchema(info.Columns), info.RowCount);
                // file уже отслеживается — см. пояснение в DataSetSourceService.CreateSourceAsync (иначе Modified вместо Added).
                db.DataSetSources.Add(added);
                updatedSourceIds.Add(added.Id);
            }
        }

        // Drop sources no longer present in the file, unless they still have bindings.
        // Распознаваемые PDF-источники (gost-*/invoice-*/gost-table:*) парсер НЕ детектит — их нельзя
        // трактовать как «исчезнувшие из файла»: данные приходят из распознавания, не из структуры.
        // Их сохраняем и помечаем устаревшими (файл заменён после распознавания) — vision-перераспознавание
        // запускается только явным действием пользователя, не автоматически при замене файла.
        var staleRecognitionSources = 0;
        foreach (var src in file.Sources.Where(s => !updatedSourceIds.Contains(s.Id)).ToList())
        {
            if (PdfProfiles.IsRecognitionMarker(src.SheetOrPath))
            {
                src.MarkRecognitionStale();
                staleRecognitionSources++;
                continue;
            }
            var hasBindings = await db.DataSetBindings.AnyAsync(b => b.SourceId == src.Id, ct);
            if (!hasBindings) db.DataSetSources.Remove(src);
        }

        file.UpdateBlobPath(newBlobPath, format);
        if (!string.IsNullOrWhiteSpace(input.Name)) file.UpdateName(input.Name);

        await db.SaveChangesAsync(ct);

        if (staleRecognitionSources > 0)
            await notifications.PublishAsync(NotificationSeverity.Warning,
                "Файл набора обновлён — нужно перераспознать",
                $"Файл «{file.Name}» заменён. Распознанные PDF-источники ({staleRecognitionSources}) помечены устаревшими: " +
                "данные относятся к прежнему файлу. Перераспознайте их вручную (кнопка «Распознать»).",
                "Наборы данных", ct: ct);

        return DataSetDtoMapper.MapFile(file);
    }

    public async Task<FileDownloadDto?> DownloadFileAsync(Guid id, CancellationToken ct)
    {
        var file = await db.DataSetFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct);
        if (file == null) return null;

        // Original extension from blobPath (format: bucket/yyyy/MM/dd/{guid}_{originalName}).
        var blobFileName = file.BlobPath.Split('/').Last();
        var underscoreIdx = blobFileName.IndexOf('_');
        var originalName = underscoreIdx >= 0 ? blobFileName[(underscoreIdx + 1)..] : blobFileName;
        var originalExt = Path.GetExtension(originalName);
        var downloadName = string.IsNullOrEmpty(originalExt) ? file.Name : $"{file.Name}{originalExt}";

        var contentType = file.Format switch
        {
            DataSetFormat.Csv  => "text/csv",
            DataSetFormat.Xlsx => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            DataSetFormat.Xls  => "application/vnd.ms-excel",
            DataSetFormat.Xml  => "application/xml",
            DataSetFormat.Json => "application/json",
            DataSetFormat.Zip  => "application/zip",
            DataSetFormat.Pdf  => "application/pdf",
            _                  => "application/octet-stream",
        };

        var stream = await blob.DownloadAsync(file.BlobPath, ct);
        return new FileDownloadDto(stream, contentType, downloadName);
    }

    public async Task<bool> DeleteFileAsync(Guid id, CancellationToken ct)
    {
        var file = await db.DataSetFiles.FindAsync([id], ct);
        if (file == null) return false;

        try { await blob.DeleteAsync(file.BlobPath, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "Не удалось удалить blob при удалении файла {FileId}", id); }

        db.DataSetFiles.Remove(file);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
