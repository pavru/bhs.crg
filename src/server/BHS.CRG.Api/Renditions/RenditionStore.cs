using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Storage;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Renditions;
using BHS.CRG.Modules.Files;
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
/// слишком велик, конвертер его не открыл. Строить заново на каждый вопрос незачем; повтор — явным
/// действием. Отказ о СЕРВИСЕ (занят, молчит, не настроен) не записывается: запись «конвертера нет»
/// пережила бы его настройку. Не записывается и «файл другого вида»: это ответ версии приложения.
/// Правило — <see cref="RenditionRefusalRules.Remembered" />.</para>
///
/// <para><b>Построение не пишет в строку владельца файла</b> — ни в счёт, ни куда-либо ещё, кроме
/// своей таблицы. Версия счёта — версия строки: запись образа в неё дала бы «счёт тем временем
/// изменили» на форме, которую человек открыл до загрузки. По той же причине у службы свой контекст
/// базы, а не контекст запроса: сохранение здесь не должно вынести в базу чужие несохранённые правки.</para>
///
/// <para><b>Профиль печати</b> (issue #1454) снимается в начале построения, до конвертера: правка
/// профиля посреди построения не должна дать образ, построенный по одним параметрам и записанный под
/// другими. Первое построение — по системному профилю; перестроение — по названному, а без названия по
/// тому, что помнит запись, пока он есть.</para>
///
/// <para>Блокировка по пути — в процессе: два одновременных вопроса об одном файле не запустят
/// конвертер дважды. От второго процесса защищает уникальность ключа в таблице: проигравший убирает
/// свой образ и отдаёт запись победителя.</para>
///
/// <para><b>Владелец</b> (ТЗ CORE-37.2). Оригинал служба читает от имени того, кто спрашивает, — его
/// называет порт модуля (<c>ModuleRenditionsPort</c> знает код модуля по построению), а не строка в
/// коде службы. Образ ложится с тем же владельцем: модуль открывает его своим портом. Запись образа
/// показывается, только если оригинал принадлежит спрашивающему, — иначе по ней узнавали бы о чужом
/// файле.</para>
/// </summary>
public sealed class RenditionStore(
    IServiceScopeFactory scopes, IBlobStorage blobs, RenditionService builder, ILogger<RenditionStore> log)
{
    /// <summary>Имя, под которым образ ложится в хранилище; путь к нему дополняется датой и guid.</summary>
    public const string ImageFileName = "rendition.pdf";

    private readonly PathLocks _locks = new();

    /// <summary>
    /// Что записано об образе этого файла; <c>null</c> — ничего, или оригинал не принадлежит
    /// <paramref name="owner" />. Не строит ничего никогда.
    /// </summary>
    public async Task<RenditionRecord?> FindAsync(string originalPath, string owner, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await Owned(db, owner).FirstOrDefaultAsync(e => e.OriginalBlobPath == originalPath, ct);
    }

    /// <summary>Записи образов, чей оригинал принадлежит владельцу, — одним запросом с самой записью.</summary>
    private static IQueryable<RenditionRecord> Owned(AppDbContext db, string owner) =>
        db.Renditions.AsNoTracking()
            .Where(e => db.BlobRegistry.Any(b => b.Path == e.OriginalBlobPath && b.Owner == owner));

    /// <summary>
    /// Образ этого файла: записанный, а если записи нет — построенный сейчас. Оригинала по пути нет —
    /// отказ «файл не найден», как у любого чтения хранилища.
    /// </summary>
    /// <param name="owner">От чьего имени читать оригинал — его владелец.</param>
    public async Task<RenditionRecord> EnsureAsync(string originalPath, string owner, CancellationToken ct)
    {
        if (await FindAsync(originalPath, owner, ct) is { } known) return known;

        using var held = await _locks.EnterAsync(originalPath, ct);
        // Пока ждали блокировку, образ мог построить тот, кто её держал.
        if (await FindAsync(originalPath, owner, ct) is { } built) return built;

        var (fresh, remember) = await BuildAsync(originalPath, owner, await ProfileAsync(null, null, ct), ct);
        return remember ? await SaveAsync(fresh, owner, replace: false) : fresh;
    }

    /// <summary>
    /// Построить образ заново — явное действие человека. Новый образ ложится под новым путём.
    ///
    /// <para>Прежний образ здесь не удаляется: помнит ли о нём кто-то (распознавание — по какому
    /// образу читало), служба не знает. Это знает уборка осиротевших: образ, на который не осталось
    /// ссылок, она уберёт, а тот, что ещё назван в чьей-то записи, оставит.</para>
    ///
    /// <para>⚠️ Отказ построенный образ не затирает — никакой. Файл тот же, что и тогда, когда образ
    /// вышел; значит, отказ сейчас говорит о сегодняшнем конвертере (занят, упал, не успел), а не о
    /// файле. Затри мы запись — образ, по которому распознаны значения, остался бы без держателя и
    /// ушёл бы следующей уборкой. Человек получает причину отказа, запись остаётся прежней.</para>
    /// </summary>
    /// <param name="profileId">Профиль печати, выбранный человеком. Не назван — тот, по которому строился
    /// прежний образ, пока он есть, иначе системный. Назван несуществующий — «не найдено», а не молча
    /// системный: человек выбрал раскладку и должен узнать, что её нет.</param>
    /// <param name="owner">От чьего имени читать оригинал — его владелец.</param>
    public async Task<RenditionRecord> RebuildAsync(
        string originalPath, string owner, Guid? profileId, CancellationToken ct)
    {
        using var held = await _locks.EnterAsync(originalPath, ct);
        var remembered = profileId is null ? (await FindAsync(originalPath, owner, ct))?.LayoutProfileId : null;
        var (fresh, remember) = await BuildAsync(
            originalPath, owner, await ProfileAsync(profileId, remembered, ct), ct);
        if (fresh.State == RenditionState.Refused
            && await FindAsync(originalPath, owner, ct) is { State: RenditionState.Built })
        {
            log.LogWarning("Перестроить образ не удалось ({Kind}): прежний образ оставлен", fresh.RefusalKind);
            return fresh;
        }
        return remember ? await SaveAsync(fresh, owner, replace: true) : fresh;
    }

    /// <summary>Профиль печати, если он есть; <c>null</c> — удалён (или не было). Для показа записи.</summary>
    public async Task<RenditionLayoutProfile?> ProfileOfAsync(Guid id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RenditionLayoutProfiles>().FindAsync(id, ct);
    }

    /// <summary>Запись образа вместе с профилем, по которому он построен, — для показа.</summary>
    /// <param name="Profile"><c>null</c> — профиль не записан или удалён: что из двух, говорит
    /// <see cref="RenditionRecord.LayoutProfileId" />.</param>
    public sealed record Shown(RenditionRecord Record, RenditionLayoutProfile? Profile);

    /// <summary>
    /// Что записано об образе — вместе с профилем, одним запросом (ревью PR #1472). Это горячий путь:
    /// состояние распознавания счёта опрашивается поллингом, и второй запрос за профилем на каждый
    /// опрос был бы платой за строку, которую показывают раз. Профиль ищется по идентификатору без
    /// отбора — тем же правилом, что <see cref="RenditionLayoutProfiles.FindAsync" />.
    /// </summary>
    public async Task<Shown?> FindShownAsync(string originalPath, string owner, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await (
                from record in Owned(db, owner).Where(e => e.OriginalBlobPath == originalPath)
                join profile in db.RenditionLayoutProfiles.AsNoTracking() on record.LayoutProfileId equals (Guid?)profile.Id
                    into profiles
                from profile in profiles.DefaultIfEmpty()
                select new { record, profile })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : new(row.record, row.profile);
    }

    /// <summary>Чем строить: профиль и его снимок, снятые до построения.</summary>
    private sealed record Chosen(Guid Id, string Stamp, PageProfile Page);

    private async Task<Chosen> ProfileAsync(Guid? named, Guid? remembered, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var profile = await scope.ServiceProvider.GetRequiredService<RenditionLayoutProfiles>()
            .ForBuildAsync(named, remembered, ct);
        if (profile is null)
        {
            // Системный создаёт старт; нет его — старт прошёл не так, но образ из-за этого не отказ.
            log.LogWarning("Системного профиля печати в базе нет: образ строится по заводским параметрам");
            profile = RenditionLayoutProfile.System();
        }
        return new(profile.Id, profile.Stamp, PageProfile.From(profile));
    }

    /// <summary>Построить и, если образ есть, положить его в хранилище. В базу не пишет.</summary>
    private async Task<(RenditionRecord Record, bool Remember)> BuildAsync(
        string originalPath, string owner, Chosen profile, CancellationToken ct)
    {
        Rendition result;
        await using (var original = await blobs.DownloadAsync(originalPath, owner, ct))
        {
            // Сначала — только начало: скану или PDF его хватает, и тянуть из хранилища десятки
            // мегабайт ради ответа «читается сам» незачем.
            var head = new byte[FileKinds.HeadBytes];
            var read = await original.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
            if (await builder.ByHeadAsync(head.AsMemory(0, read), ct) is { } byHead) result = byHead;
            else
            {
                // Целиком — во временный файл, а не в память: оригинал бывает до 50 МБ, а нужен ли
                // он весь, решит служба (файл больше предела к конвертеру не пойдёт).
                await using var copy = new FileStream(
                    Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()), FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
                await copy.WriteAsync(head.AsMemory(0, read), ct);
                await original.CopyToAsync(copy, ct);
                result = await builder.BuildAsync(copy, profile.Page, ct);
            }
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
                    // Образ — того же владельца, что оригинал: модуль открывает его своим портом.
                    var path = await blobs.UploadAsync(ImageFileName, pdf, "application/pdf", owner, ct);
                    // Профиль помнит только книга, которой его раскладку подставили. Word и .xls по
                    // нему не строятся, книга со своей раскладкой его не получала, а неудавшаяся
                    // правка ушла как есть — у всех них «профиль изменён» обещал бы перестроением
                    // другой вид, которого не будет (ревью PR #1472).
                    var byProfile = image.Layout == RenditionLayout.Applied;
                    return (RenditionRecord.Built(originalPath, path, image.Pages, image.Notes, image.Converter,
                        image.Layout, image.LayoutApplied,
                        byProfile ? profile.Id : null, byProfile ? profile.Stamp : null), true);
                }
            case Rendition.Refused refused:
                if (!refused.Kind.Remembered())
                    log.LogInformation("Читаемый образ не построен, отказ не записан ({Kind})", refused.Kind);
                return (RenditionRecord.Refused(originalPath, refused.Kind.ToString(), refused.Reason), refused.Kind.Remembered());
            default:
                throw new InvalidOperationException($"Неизвестный ответ службы образов: {result.GetType().Name}");
        }
    }

    /// <summary>
    /// Записать результат. Токена отмены здесь нет намеренно: образ к этому моменту уже лежит в
    /// хранилище, и отмена оставила бы его без записи — построенным зря.
    ///
    /// <para>⚠️ <b>Оригинал могли удалить, пока строился образ</b> — построение длится секунду, а
    /// файл заменяют сразу после загрузки. Удаление записи образа тогда не нашло (её ещё не было),
    /// и запись, сделанная после него, держала бы образ оригинала, которого нет: уборка такую не
    /// убрала бы никогда. Поэтому после записи спрашиваем реестр. Порядок закрывает гонку без общей
    /// блокировки: удаление сначала снимает путь с реестра, потом убирает запись образа. Сняло до
    /// нашего вопроса — убираем за собой сами; сняло после — наша запись уже лежит, и удаление её
    /// найдёт.</para>
    /// </summary>
    private async Task<RenditionRecord> SaveAsync(RenditionRecord fresh, string owner, bool replace)
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
            var saved = existing ?? fresh;
            if (await db.BlobRegistry.AnyAsync(e => e.Path == saved.OriginalBlobPath)) return saved;

            await db.Renditions.Where(e => e.Id == saved.Id).ExecuteDeleteAsync();
            if (saved.ImageBlobPath is { } late) await blobs.DeleteAsync(late, owner);
            // Оригинала больше нет, и отвечает на это хранилище — своим «файл не найден», тем же,
            // что получил бы вопрос об этом файле, заданный секундой позже. Своего отказа служба
            // не придумывает. Строка после — для компилятора: сюда доходят, только если путь за
            // это мгновение вернули на место, а тогда и ответ «записи нет, спросите снова» верен.
            await using var gone = await blobs.DownloadAsync(saved.OriginalBlobPath, owner);
            return saved;
        }
        catch (DbUpdateException ex) when (DbFailure.IsUniqueViolation(ex))
        {
            // Запись успел сделать другой процесс. Его образ и остаётся: у пути запись одна, и
            // «как есть» значит — та, что легла первой.
            if (fresh.ImageBlobPath is { } own) await blobs.DeleteAsync(own, owner);
            db.ChangeTracker.Clear();
            return await db.Renditions.AsNoTracking().FirstAsync(e => e.OriginalBlobPath == fresh.OriginalBlobPath);
        }
    }

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
