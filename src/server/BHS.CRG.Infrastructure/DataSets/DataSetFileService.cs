using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Notifications;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Common;
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
    SystemSourceCounter systemCounts,
    ILogger<DataSetFileService> logger,
    INotificationService notifications)
{
    /// <summary>
    /// Наборы уровня и всех его родителей (issue #721). Цепочку резолвит общий
    /// <see cref="ScopeChains.LoadForScopeAsync"/> — тот самый, что заведён «взамен разрозненных
    /// копий резолва/фильтра»; своя копия здесь и была бы такой копией. Отбор всё же пишется
    /// запросом, а не <see cref="ScopeChain.Contains"/>: тот в SQL не переводится, а тянуть все
    /// наборы всех строек в память ради фильтра в клиенте — не вариант.
    ///
    /// Неразрешённый уровень цепочка отдаёт как <c>Guid.Empty</c>, поэтому проверяем именно на него:
    /// у наборов такого идентификатора нет, но без явной проверки условие уровня стало бы всегда
    /// истинным и в выборку попали бы чужие ветки.
    /// </summary>
    private static IQueryable<DataSetFile> ChainFilter(IQueryable<DataSetFile> q, ScopeChain chain) =>
        q.Where(f =>
            (f.Scope == CatalogScope.System && f.ScopeId == null) ||
            (chain.ConstructionId != Guid.Empty && f.Scope == CatalogScope.Construction && f.ScopeId == chain.ConstructionId) ||
            (chain.SectionId != Guid.Empty && f.Scope == CatalogScope.Section && f.ScopeId == chain.SectionId) ||
            (chain.SetId != Guid.Empty && f.Scope == CatalogScope.Set && f.ScopeId == chain.SetId));

    public async Task<IReadOnlyList<DataSetFileDto>> ListFilesAsync(
        string? scope, Guid? scopeId, bool includeInherited, CancellationToken ct)
    {
        var q = db.DataSetFiles.Include(f => f.Sources).AsNoTracking().AsQueryable();
        if (scope != null && Enum.TryParse<CatalogScope>(scope, out var s))
        {
            // Наборы верхних уровней доступны на нижних (issue #721): комплект пользуется наборами
            // своего раздела, стройки и системы. Экран уровня показывал только своё, а селектор
            // источника у привязки — всю цепочку; расхождение никто не задумывал.
            q = includeInherited
                ? ChainFilter(q, await ScopeChains.LoadForScopeAsync(db, s, scopeId, ct))
                : q.Where(f => f.Scope == s && f.ScopeId == scopeId);
        }

        var files = await q.OrderBy(f => f.Name).ToListAsync(ct);

        // Число привязок на источник (issue #417) — ОДНИМ групповым запросом по всем источникам
        // выборки: на альбоме с десятком источников запрос-на-источник дал бы N+1.
        var sourceIds = files.SelectMany(f => f.Sources.Select(x => x.Id)).ToList();
        var bindingCounts = sourceIds.Count == 0
            ? new Dictionary<Guid, int>()
            : await db.DataSetBindings.AsNoTracking()
                .Where(b => sourceIds.Contains(b.SourceId))
                .GroupBy(b => b.SourceId)
                .ToDictionaryAsync(g => g.Key, g => g.Count(), ct);

        // Строки системных наборов живые — их число считаем заново, а не показываем запомненное
        // при создании источника (issue #613).
        var liveStates = await systemCounts.StateAsync(files, ct);

        return files.Select(f => DataSetDtoMapper.MapFile(f, bindingCounts, liveStates)).ToList();
    }

    public async Task<IReadOnlyList<DataSetFileDto>> ListAvailableFilesAsync(Guid setId, CancellationToken ct)
    {
        if (!await db.Set<DocumentSet>().AsNoTracking().AnyAsync(s => s.Id == setId, ct))
            throw new NotFoundException("DocumentSet не найден");

        var files = await ChainFilter(
                db.DataSetFiles.Include(f => f.Sources).AsNoTracking(),
                await ScopeChains.LoadAsync(db, setId, ct))
            .OrderBy(f => f.Scope).ThenBy(f => f.Name)
            .ToListAsync(ct);

        var liveStates = await systemCounts.StateAsync(files, ct);
        return files.Select(f => DataSetDtoMapper.MapFile(f, bindingCounts: null, liveStates)).ToList();
    }

    public async Task<DataSetFileDto> UploadFileAsync(UploadFileInput input, CancellationToken ct)
    {
        if (!Enum.TryParse<CatalogScope>(input.Scope, out var scope))
            throw new InvalidRequestException("Неверный scope");

        var format = DataSetDtoMapper.DetectFormat(input.FileName)
            ?? throw new InvalidRequestException("Неподдерживаемый формат файла");

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

    /// <summary>
    /// Системный набор (issue #580): сырьё — данные самой системы в границах scope, а не файл.
    /// Идемпотентен: на один уровень такой набор нужен один, источники к нему добавляются отдельно.
    /// </summary>
    public async Task<DataSetFileDto> CreateSystemFileAsync(CreateSystemFileInput input, CancellationToken ct)
    {
        if (!Enum.TryParse<CatalogScope>(input.Scope, out var scope))
            throw new InvalidRequestException("Неверный scope");

        Guid? scopeId = scope != CatalogScope.System && Guid.TryParse(input.ScopeId, out var sid) ? sid : null;

        var existing = await db.DataSetFiles.Include(f => f.Sources)
            .FirstOrDefaultAsync(f => f.Format == DataSetFormat.System && f.Scope == scope && f.ScopeId == scopeId, ct);
        if (existing != null) return DataSetDtoMapper.MapFile(existing);

        var name = string.IsNullOrWhiteSpace(input.Name) ? "Данные системы" : input.Name.Trim();
        var file = DataSetFile.CreateSystem(name, scope, scopeId);
        db.DataSetFiles.Add(file);
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapFile(file);
    }

    public async Task<DataSetFileDto?> ReplaceFileAsync(Guid id, ReplaceFileInput input, CancellationToken ct)
    {
        var file = await db.DataSetFiles.Include(f => f.Sources).FirstOrDefaultAsync(f => f.Id == id, ct);
        if (file == null) return null;
        if (file.IsSystem)
            throw new InvalidRequestException("У системного набора нет файла — заменять нечего.");

        var format = DataSetDtoMapper.DetectFormat(input.FileName)
            ?? throw new InvalidRequestException("Неподдерживаемый формат файла");

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
        if (file.IsSystem) return null; // скачивать нечего — блоба нет

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

        if (!file.IsSystem)
        {
            try { await blob.DeleteAsync(file.BlobPath, ct); }
            catch (Exception ex) { logger.LogWarning(ex, "Не удалось удалить blob при удалении файла {FileId}", id); }
        }

        db.DataSetFiles.Remove(file);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
