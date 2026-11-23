using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>Связывает публикацию новых файлов набора и уборку прежних с исходом записи в БД.</summary>
internal sealed class DataSetBlobWrite(
    AppDbContext db, IBlobStorage blob, ILogger logger, Guid fileId, IReadOnlyCollection<string> previous)
    : IAsyncDisposable
{
    private readonly List<string> uploaded = [];
    private bool commitStarted;
    private bool committed;

    public async Task<string> UploadAsync(string name, Stream content, string contentType, CancellationToken ct)
    {
        var path = await blob.UploadAsync(name, content, contentType, ct);
        uploaded.Add(path);
        return path;
    }

    public async Task CommitAsync(Func<Task> write, CancellationToken ct)
    {
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await write();
            commitStarted = true;
            await transaction.CommitAsync(ct);
            committed = true;
        }
        catch (DbUpdateConcurrencyException ex) when (!commitStarted)
        {
            db.ChangeTracker.Clear();
            throw new DataSetRevisionConflict(ex);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // До COMMIT транзакция могла только откатиться. После попытки COMMIT ответ мог потеряться.
        // В этом случае и при уборке прежних файлов сверяем ссылки по сохранённому набору.
        IEnumerable<string> candidates = committed ? previous.Except(uploaded) :
            commitStarted ? previous.Union(uploaded) : uploaded;
        var paths = candidates.ToHashSet(StringComparer.Ordinal);
        if (paths.Count == 0) return;

        if (commitStarted)
        {
            try { await ExcludeReferencedAsync(paths); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Не удалось сверить сохранённые файлы набора {FileId}", fileId);
                return;
            }
        }

        foreach (var path in paths)
        {
            try { await blob.DeleteAsync(path, CancellationToken.None); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Не удалось удалить файл {BlobPath} набора {FileId}", path, fileId);
            }
        }
    }

    private async Task ExcludeReferencedAsync(HashSet<string> paths)
    {
        // Дождаться завершения возможного COMMIT и прочитать исходник, разбиение и проекции
        // согласованно. Иначе первый SELECT мог бы ещё увидеть данные до завершения записи.
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM dataset_files WHERE "Id" = {fileId} FOR UPDATE""");
        var file = await db.DataSetFiles.Where(f => f.Id == fileId)
            .Select(f => new { f.BlobPath, f.Grouping }).SingleOrDefaultAsync();
        if (file is null) return;

        paths.Remove(file.BlobPath);
        paths.ExceptWith(GostGroupingSerialization.ReadPartPaths(file.Grouping)
            ?? throw new InvalidOperationException("Разбиение набора не прочитано."));
        var registries = await db.DataSetSources
            .Where(s => s.FileId == fileId && s.SheetOrPath == PdfProfiles.GostDocumentsMarker)
            .Select(s => s.CachedData).ToListAsync();
        foreach (var rows in registries)
            paths.ExceptWith(GostGroupingSerialization.ReadRowFilePaths(rows)
                ?? throw new InvalidOperationException("Строки реестра документов не прочитаны."));
    }
}
