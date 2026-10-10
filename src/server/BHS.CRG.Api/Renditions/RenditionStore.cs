using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Storage;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Renditions;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Api.Renditions;

/// <summary>
/// Читаемые образы файлов: построить один раз, записать и дальше отдавать записанное (issue #1269).
///
/// <para><b>Запись есть — отдаётся как есть</b>, каким бы старым конвертером она ни сделана. Образ,
/// который молча перестроился после обновления, разорвал бы пару «что видел человек — что читала
/// модель»: значения распознаны по одному PDF, а на экране уже другой. Перестроить — явное действие
/// (<see cref="RebuildAsync" />), и новый образ ложится под новым путём.</para>
///
/// <para><b>Отказ тоже запись</b> — но только тот, что говорит о ФАЙЛЕ: под паролем, пуст, повреждён,
/// слишком велик. Он не изменится, сколько ни спрашивай, и строить заново на каждый показ незачем.
/// Отказ о СЕРВИСЕ (занят, молчит, не настроен) не записывается: завтра тот же файл получит образ, а
/// запись «конвертера нет» пережила бы его настройку и требовала бы нажать «Перестроить» у каждого
/// файла по одному.</para>
///
/// <para><b>Построение не пишет в строку владельца файла</b> — ни в счёт, ни куда-либо ещё, кроме
/// своей таблицы. Версия счёта — версия строки: запись образа в неё дала бы «счёт тем временем
/// изменили» на форме, которую человек открыл до загрузки. По той же причине у службы свой контекст
/// базы, а не контекст запроса: сохранение здесь не должно вынести в базу чужие несохранённые правки.</para>
///
/// <para>Блокировка по пути — в процессе: два одновременных вопроса об одном файле не запустят
/// конвертер дважды. От второго процесса защищает уникальность ключа в таблице: проигравший убирает
/// свой образ и отдаёт запись победителя.</para>
/// </summary>
public sealed class RenditionStore(
    IServiceScopeFactory scopes, IBlobStorage blobs, RenditionService builder, ILogger<RenditionStore> log)
{
    /// <summary>Имя, под которым образ ложится в хранилище; путь к нему дополняется датой и guid.</summary>
    public const string ImageFileName = "rendition.pdf";

    private readonly PathLocks _locks = new();

    /// <summary>Что записано об образе этого файла; <c>null</c> — ничего. Не строит ничего никогда.</summary>
    public async Task<RenditionRecord?> FindAsync(string originalPath, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Renditions.AsNoTracking().FirstOrDefaultAsync(e => e.OriginalBlobPath == originalPath, ct);
    }

    /// <summary>
    /// Образ этого файла: записанный, а если записи нет — построенный сейчас. Оригинала по пути нет —
    /// отказ «файл не найден», как у любого чтения хранилища.
    /// </summary>
    public async Task<RenditionRecord> EnsureAsync(string originalPath, CancellationToken ct)
    {
        if (await FindAsync(originalPath, ct) is { } known) return known;

        using var held = await _locks.EnterAsync(originalPath, ct);
        // Пока ждали блокировку, образ мог построить тот, кто её держал.
        if (await FindAsync(originalPath, ct) is { } built) return built;

        var (fresh, remember) = await BuildAsync(originalPath, ct);
        return remember ? await SaveAsync(fresh, replace: false) : fresh;
    }

    /// <summary>
    /// Построить образ заново — явное действие человека. Новый образ ложится под новым путём.
    ///
    /// <para>Прежний образ здесь не удаляется: помнит ли о нём кто-то (распознавание — по какому
    /// образу читало), служба не знает. Это знает уборка осиротевших: образ, на который не осталось
    /// ссылок, она уберёт, а тот, что ещё назван в чьей-то записи, оставит.</para>
    ///
    /// <para>Отказ о сервисе прежнюю запись не трогает: «конвертер занят» — не причина потерять образ,
    /// который был.</para>
    /// </summary>
    public async Task<RenditionRecord> RebuildAsync(string originalPath, CancellationToken ct)
    {
        using var held = await _locks.EnterAsync(originalPath, ct);
        var (fresh, remember) = await BuildAsync(originalPath, ct);
        return remember ? await SaveAsync(fresh, replace: true) : fresh;
    }

    /// <summary>Построить и, если образ есть, положить его в хранилище. В базу не пишет.</summary>
    private async Task<(RenditionRecord Record, bool Remember)> BuildAsync(string originalPath, CancellationToken ct)
    {
        Rendition result;
        // Во временный файл, а не в память: оригинал — до 50 МБ, и скану или PDF, которым хватает
        // первых килобайт, незачем лежать в памяти целиком.
        await using (var copy = new FileStream(
            Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()), FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous))
        {
            await using (var original = await blobs.DownloadAsync(originalPath, ct))
                await original.CopyToAsync(copy, ct);
            result = await builder.BuildAsync(copy, ct);
        }

        switch (result)
        {
            case Rendition.AsIs asIs:
                return (RenditionRecord.AsIs(originalPath, asIs.Mime), true);
            case Rendition.Built image:
                // Сначала образ в хранилище, потом запись о нём. Упавшее между ними даёт файл без
                // ссылки — его найдёт уборка осиротевших; обратный порядок дал бы запись, которая
                // обещает образ и не открывает его.
                using (var pdf = new MemoryStream(image.Pdf, writable: false))
                {
                    var path = await blobs.UploadAsync(ImageFileName, pdf, "application/pdf", ct);
                    return (RenditionRecord.Built(originalPath, path, image.Pages, image.Notes, image.Converter), true);
                }
            case Rendition.Refused refused:
                if (refused.AboutService)
                    log.LogWarning("Читаемый образ не построен и не записан: отказ сервиса ({Kind})", refused.Kind);
                return (RenditionRecord.Refused(originalPath, refused.Kind.ToString(), refused.Reason), !refused.AboutService);
            default:
                throw new InvalidOperationException($"Неизвестный ответ службы образов: {result.GetType().Name}");
        }
    }

    /// <summary>
    /// Записать результат. Токена отмены здесь нет намеренно: образ к этому моменту уже лежит в
    /// хранилище, и отмена оставила бы его без записи — построенным зря.
    /// </summary>
    private async Task<RenditionRecord> SaveAsync(RenditionRecord fresh, bool replace)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        try
        {
            var existing = replace
                ? await db.Renditions.FirstOrDefaultAsync(e => e.OriginalBlobPath == fresh.OriginalBlobPath)
                : null;
            if (existing is null) db.Renditions.Add(fresh);
            else existing.ReplaceWith(fresh);
            await db.SaveChangesAsync();
            return existing ?? fresh;
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            // Запись успел сделать другой процесс. Его образ и остаётся: у пути запись одна, и
            // «как есть» значит — та, что легла первой.
            if (fresh.ImageBlobPath is { } own) await blobs.DeleteAsync(own);
            db.ChangeTracker.Clear();
            return await db.Renditions.AsNoTracking().FirstAsync(e => e.OriginalBlobPath == fresh.OriginalBlobPath);
        }
    }

    /// <summary>Нарушение уникальности (код 23505 у Postgres).</summary>
    private static bool IsDuplicateKey(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };

    /// <summary>
    /// Блокировки по ключу: у каждого пути свои ворота, и ждут в них только те, кто спрашивает об
    /// одном и том же файле. Ворота живут, пока ими пользуются, — иначе словарь рос бы с каждым
    /// файлом, который когда-либо приложили.
    /// </summary>
    private sealed class PathLocks
    {
        private sealed class Gate
        {
            public readonly SemaphoreSlim Door = new(1, 1);
            public int Users;
        }

        private readonly Dictionary<string, Gate> _gates = new(StringComparer.Ordinal);

        public async Task<IDisposable> EnterAsync(string path, CancellationToken ct)
        {
            Gate gate;
            lock (_gates)
            {
                if (!_gates.TryGetValue(path, out gate!)) _gates[path] = gate = new Gate();
                gate.Users++;
            }
            try
            {
                await gate.Door.WaitAsync(ct);
            }
            catch
            {
                Leave(path, gate, entered: false);
                throw;
            }
            return new Held(this, path, gate);
        }

        private void Leave(string path, Gate gate, bool entered)
        {
            if (entered) gate.Door.Release();
            lock (_gates)
            {
                if (--gate.Users == 0) _gates.Remove(path);
            }
        }

        private sealed class Held(PathLocks owner, string path, Gate gate) : IDisposable
        {
            private int _left;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _left, 1) == 0) owner.Leave(path, gate, entered: true);
            }
        }
    }
}
