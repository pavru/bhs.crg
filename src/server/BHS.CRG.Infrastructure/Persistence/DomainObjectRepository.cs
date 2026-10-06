using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Persistence;

/// <summary>
/// Репозиторий единого <see cref="DomainObject"/> (issue #84). Грузит документную фасету и её
/// сгенерированные файлы — для общих данных фасета просто отсутствует (null).
/// </summary>
public class DomainObjectRepository(AppDbContext db) : Repository<DomainObject>(db), IDomainObjectRepository
{
    public override Task<DomainObject?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Db.Set<DomainObject>()
            .Include(o => o.Facet)
            .ThenInclude(f => f!.GeneratedFiles)
            .FirstOrDefaultAsync(o => o.Id == id, ct);

    public async Task SaveSeenAsync(DomainObject entry, string seen, CancellationToken ct = default)
    {
        // Своя транзакция — если вызывающий не открыл её сам: блокировка строки живёт до фиксации.
        await using var own = Db.Database.CurrentTransaction is null
            ? await Db.Database.BeginTransactionAsync(ct)
            : null;
        // Версию читаем ТЕМ ЖЕ запросом, что берёт блокировку: прочитанная до неё была бы прошлым.
        // «xid» приводится через текст — прямого приведения к числу у него нет.
        var stored = await Db.Database
            .SqlQuery<long>($"""SELECT xmin::text::bigint AS "Value" FROM domain_objects WHERE "Id" = {entry.Id} FOR UPDATE""")
            .ToListAsync(ct);
        if (stored.Count == 0) throw new BHS.CRG.Domain.Common.NotFoundException();
        RecordSeen.Ensure(stored[0].ToString(System.Globalization.CultureInfo.InvariantCulture), seen);

        await Db.SaveChangesAsync(ct);
        if (own is not null) await own.CommitAsync(ct);
    }

    public async Task<ILockedObjects> ReadForUpdateAsync(
        System.Linq.Expressions.Expression<Func<DomainObject, bool>> which, CancellationToken ct = default)
    {
        var own = await OwnTransactionAsync(ct);
        try
        {
            // Отбор — внутри той же транзакции, что и блокировка; строка, заведённая после него,
            // в работу не попадёт — как не попадала и раньше.
            var ids = await Db.Set<DomainObject>().Where(which).Select(o => o.Id).ToListAsync(ct);
            return await LockAndReadAsync(ids, own, ct);
        }
        catch
        {
            if (own is not null) await own.DisposeAsync();
            throw;
        }
    }

    public async Task<ILockedObjects> ReadForUpdateAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        var own = await OwnTransactionAsync(ct);
        try
        {
            return await LockAndReadAsync(ids, own, ct);
        }
        catch
        {
            if (own is not null) await own.DisposeAsync();
            throw;
        }
    }

    /// <summary>Своя транзакция — если вызывающий не открыл её сам: блокировка строки живёт до фиксации.</summary>
    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> OwnTransactionAsync(CancellationToken ct) =>
        Db.Database.CurrentTransaction is null ? await Db.Database.BeginTransactionAsync(ct) : null;

    private async Task<ILockedObjects> LockAndReadAsync(
        IReadOnlyCollection<Guid> ids, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? own, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToArray();
        // В порядке идентификаторов: два писателя, взявшие одни и те же строки в разном порядке,
        // ждали бы друг друга вечно.
        await Db.Database
            .SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM domain_objects WHERE "Id" = ANY({wanted}) ORDER BY "Id" FOR UPDATE""")
            .ToListAsync(ct);

        // Объект, который контекст уже держит, запрос вернул бы ПРЕЖНИМ: отслеживаемую сущность EF
        // значениями из базы не обновляет. Поэтому такие перечитываются явно — иначе блокировка
        // охраняла бы запись того же устаревшего снимка.
        foreach (var entry in Db.ChangeTracker.Entries<DomainObject>().Where(e => wanted.Contains(e.Entity.Id)).ToList())
            if (entry.State is not (EntityState.Added or EntityState.Detached))
                await entry.ReloadAsync(ct);

        var objects = await Db.Set<DomainObject>().Where(o => wanted.Contains(o.Id)).ToListAsync(ct);
        return new LockedObjects(Db, own, objects);
    }

    private sealed class LockedObjects(
        AppDbContext db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? own,
        IReadOnlyList<DomainObject> objects) : ILockedObjects
    {
        public IReadOnlyList<DomainObject> Objects => objects;

        public async Task SaveAsync(CancellationToken ct = default)
        {
            await db.SaveChangesAsync(ct);
            if (own is not null) await own.CommitAsync(ct);
        }

        // Незафиксированная транзакция при освобождении откатывается — записано не будет ничего.
        public async ValueTask DisposeAsync()
        {
            if (own is not null) await own.DisposeAsync();
        }
    }

    public async Task<IReadOnlyList<DomainObject>> GetSetDocumentsAsync(Guid setId, bool tracked, CancellationToken ct = default)
    {
        var q = Db.Set<DomainObject>()
            .Include(o => o.Facet).ThenInclude(f => f!.GeneratedFiles)
            .Where(o => o.ScopeLevel == CatalogScope.Set && o.ScopeId == setId && o.Facet != null);
        if (!tracked) q = q.AsNoTracking();
        return await q.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DomainObject>> GetDocumentsInSetsAsync(IReadOnlyCollection<Guid> setIds, CancellationToken ct = default)
        => await Db.Set<DomainObject>()
            .AsNoTracking()
            .Include(o => o.Facet)
            .Where(o => o.ScopeLevel == CatalogScope.Set && o.ScopeId != null && setIds.Contains(o.ScopeId.Value) && o.Facet != null)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<DomainObject>> GetDocumentsOfTypeAsync(Guid documentTypeId, CancellationToken ct = default)
        => await Db.Set<DomainObject>()
            .Include(o => o.Facet).ThenInclude(f => f!.GeneratedFiles)
            .Where(o => o.CompositeTypeId == documentTypeId && o.Facet != null)
            .ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, int>> CountDocumentsInSetsAsync(IReadOnlyCollection<Guid> setIds, CancellationToken ct = default)
    {
        if (setIds.Count == 0) return new Dictionary<Guid, int>();
        // Только COUNT по оси (Set, ScopeId) с наличием фасеты — без загрузки Data/JSONB (лёгкий счётчик
        // для навигации/каскадов; сами документы — DomainObject по расположению, прямой навигации нет).
        var rows = await Db.Set<DomainObject>()
            .AsNoTracking()
            .Where(o => o.ScopeLevel == CatalogScope.Set && o.ScopeId != null && setIds.Contains(o.ScopeId.Value) && o.Facet != null)
            .GroupBy(o => o.ScopeId!.Value)
            .Select(g => new { SetId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.SetId, r => r.Count);
    }

    public async Task<IReadOnlyDictionary<(Guid SetId, Guid TypeId), int>> CountReadyDocumentsByTypeAsync(
        IReadOnlyCollection<Guid> setIds, CancellationToken ct = default)
    {
        if (setIds.Count == 0) return new Dictionary<(Guid, Guid), int>();

        var rows = await Db.Set<DomainObject>()
            .AsNoTracking()
            .Where(o => o.ScopeLevel == CatalogScope.Set && o.ScopeId != null && setIds.Contains(o.ScopeId.Value)
                        && o.Facet != null && o.Facet.Status == DocumentStatus.Generated)
            .GroupBy(o => new { SetId = o.ScopeId!.Value, TypeId = o.CompositeTypeId })
            .Select(g => new { g.Key.SetId, g.Key.TypeId, Count = g.Count() })
            .ToListAsync(ct);

        return rows.ToDictionary(r => (r.SetId, r.TypeId), r => r.Count);
    }

    public async Task<IReadOnlyList<CommonDataRef>> RefsByIdsAsync(
        IReadOnlyCollection<Guid> typeIds, IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (typeIds.Count == 0 || ids.Count == 0) return [];

        // Показ уже стоящих ссылок: архивные на месте — ссылка на них цела и обязана читаться.
        return await Db.Set<DomainObject>()
            .AsNoTracking()
            .Where(o => o.Facet == null && typeIds.Contains(o.CompositeTypeId) && ids.Contains(o.Id))
            .OrderBy(o => o.DisplayName)
            .Select(o => new CommonDataRef(o.Id, o.CompositeTypeId, o.DisplayName, o.ArchivedAt != null, null))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ArchivedRecord>> ArchivedAmongAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];
        // Показ: спрашивают про уже стоящие ссылки, и ответ — сам признак архива.
        return await Db.Set<DomainObject>().AsNoTracking()
            .Where(o => o.Facet == null && o.ArchivedAt != null && ids.Contains(o.Id))
            .Select(o => new ArchivedRecord(o.Id, o.DisplayName))
            .ToListAsync(ct);
    }

    public async Task<ChoiceCandidates> SearchForChoiceAsync(
        IReadOnlyCollection<Guid> typeIds, string? search, int? limit, CancellationToken ct = default)
    {
        if (typeIds.Count == 0) return ChoiceCandidates.Empty;

        // Только общие данные: документная фасета здесь ни при чём — у записи справочника её нет, а
        // без этого условия в выбор позиции попали бы документы комплектов.
        var query = Db.Set<DomainObject>()
            .AsNoTracking()
            .Where(o => o.Facet == null && typeIds.Contains(o.CompositeTypeId));

        if (!string.IsNullOrWhiteSpace(search))
        {
            // ⚠️ Знаки образца обезврежены: «%» в набранном тексте иначе означал бы «что угодно», то
            // есть поиск «100%» показывал бы всё подряд и выглядел бы исправной работой.
            var pattern = "%" + search.Trim().Replace("!", "!!").Replace("%", "!%").Replace("_", "!_") + "%";
            // ⚠️ И по альтернативным именам (issue #1169): они «участвуют в сопоставлении по имени
            // наравне с названием» (DomainObject.Aliases). Без них запрос «ВВГ 3*2.5» к позиции
            // «Кабель ВВГнг(А)-LS 3х2,5» отвечал пустым списком — и человек заводил дубль.
            query = query.Where(o => EF.Functions.ILike(o.DisplayName!, pattern, "!")
                                     || o.Aliases.Any(a => EF.Functions.ILike(a, pattern, "!")));
        }

        // Архив считается ТЕМ ЖЕ отбором, что и выбор, — до того, как архивные из выбора убраны.
        // Иначе «в архиве: 3» означало бы «в архиве вообще», а не «под ваш запрос», и подсказка
        // «есть в архиве — вернуть?» звала бы искать то, чего там нет.
        var inArchive = await query.CountAsync(o => o.ArchivedAt != null, ct);

        // Сортировка ДО отсечения — иначе «первые N по названию» означало бы «произвольные N»
        // (контракт метода). Сравнение здесь базы, окончательный порядок задаёт тот, кто показывает.
        var ordered = query.Where(o => o.ArchivedAt == null).OrderBy(o => o.DisplayName).AsQueryable();
        if (limit is > 0) ordered = ordered.Take(limit.Value);

        // Альтернативные имена едут только когда искали текстом: назвать «найдено по …» больше
        // некому, а без поиска они ссылке не нужны.
        if (string.IsNullOrWhiteSpace(search))
            return new ChoiceCandidates(await ordered
                .Select(o => new CommonDataRef(o.Id, o.CompositeTypeId, o.DisplayName, false, null))
                .ToListAsync(ct), inArchive);

        var needle = search.Trim();
        var found = await ordered
            .Select(o => new { o.Id, o.CompositeTypeId, o.DisplayName, o.Aliases })
            .ToListAsync(ct);

        return new ChoiceCandidates([.. found.Select(o => new CommonDataRef(o.Id, o.CompositeTypeId, o.DisplayName,
            false, MatchedAlias(o.DisplayName, o.Aliases, needle)))], inArchive);
    }

    /// <summary>
    /// По какому альтернативному имени запись найдена — если набранного нет в её названии.
    ///
    /// <para>Сравнение здесь своё, а не базы, и разойтись с <c>ILIKE</c> оно может на редких знаках.
    /// Расхождение безвредно: запись всё равно в ответе (её отобрала база), пропадёт только пояснение.</para>
    /// </summary>
    private static string? MatchedAlias(string? name, IReadOnlyList<string> aliases, string needle) =>
        name is not null && name.Contains(needle, StringComparison.OrdinalIgnoreCase)
            ? null
            : aliases.FirstOrDefault(a => a.Contains(needle, StringComparison.OrdinalIgnoreCase));
}
