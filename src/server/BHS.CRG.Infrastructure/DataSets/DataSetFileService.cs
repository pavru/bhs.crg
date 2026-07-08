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

        // Набор = только сырьё; источники создаются пользователем ЯВНО (см. issue #20 и философию
        // наборов данных). При загрузке источники НЕ авто-создаются — диалог создания источника
        // предлагает детект как подсказки (GET /files/{id}/source-candidates).
        var dataSetFile = DataSetFile.Create(name, format, blobPath, scope, scopeId);

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

        // Источники управляются пользователем ЯВНО (issue #20): при замене файла НЕ авто-создаём и
        // НЕ авто-удаляем источники — только пере-разбираем СУЩЕСТВУЮЩИЕ против нового блоба.
        var staleSources = 0;
        foreach (var src in file.Sources.ToList())
        {
            // Распознанные PDF-источники (gost-*/invoice-*/gost-table:*) не переразбираем структурно —
            // данные приходят из vision-распознавания, не из структуры файла. Помечаем устаревшими;
            // пере-распознавание — только явным действием пользователя.
            if (PdfProfiles.IsRecognitionMarker(src.SheetOrPath))
            {
                src.MarkRecognitionStale();
                staleSources++;
                continue;
            }
            try
            {
                var parsed = await parser.ParseAsync(input.Bytes, src.SheetOrPath, src.ColumnExpressions, ct);
                src.UpdateCache(DataSetDtoMapper.SerializeSchema(parsed.Columns), parsed.Rows.Count);
            }
            catch (Exception ex)
            {
                // Источник не разбирается против нового файла (лист/путь исчез или сменился формат) —
                // помечаем устаревшим, но НЕ удаляем (источник создан пользователем явно).
                logger.LogWarning(ex, "Источник {SourceId} не разобран при замене файла {FileId}", src.Id, id);
                src.MarkRecognitionStale();
                staleSources++;
            }
        }

        file.UpdateBlobPath(newBlobPath, format);
        if (!string.IsNullOrWhiteSpace(input.Name)) file.UpdateName(input.Name);

        await db.SaveChangesAsync(ct);

        if (staleSources > 0)
            await notifications.PublishAsync(NotificationSeverity.Warning,
                "Файл набора обновлён — проверьте источники",
                $"Файл «{file.Name}» заменён. Источников, требующих внимания: {staleSources} " +
                "(PDF-распознавание — перераспознайте вручную; прочие — проверьте определение источника, если данные не совпали).",
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
