using BHS.CRG.Application.Objects;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Persistence;

/// <summary>
/// Признак архива записи справочника — условным обновлением, мимо сохранения сущности
/// (issue #1185). Почему так — <see cref="IRecordArchive" />.
/// </summary>
public class RecordArchive(AppDbContext db) : IRecordArchive
{
    public async Task<ArchiveOutcome> SetAsync(Guid id, bool archived, CancellationToken ct = default)
    {
        // Условия — в самом запросе: «уже в архиве», «это документ» и «это профиль» решаются тем же
        // обновлением, что и запись, и гонки между проверкой и изменением нет. UpdatedAt не трогаем:
        // данные записи не менялись, а по этой метке судят о свежести именно их.
        DateTimeOffset? value = archived ? DateTimeOffset.UtcNow : null;
        var target = db.DomainObjects.Where(o => o.Id == id && o.Facet == null && (o.ArchivedAt != null) != archived);
        // Вернуть из архива можно и профиль: если признак на нём оказался, снять его — всегда благо.
        if (archived) target = target.Where(o => !ProfileIds().Contains(o.Id));
        if (await target.ExecuteUpdateAsync(s => s.SetProperty(o => o.ArchivedAt, value), ct) > 0)
            return ArchiveOutcome.Changed;

        // Ничего не изменилось — выясняем почему. Это чтение уже ничего не решает: оно только
        // называет причину, и устаревший ответ здесь стоит неточного слова, а не неверной записи.
        var seen = await db.DomainObjects.AsNoTracking().Where(o => o.Id == id)
            .Select(o => new { IsDocument = o.Facet != null, IsArchived = o.ArchivedAt != null })
            .FirstOrDefaultAsync(ct);
        if (seen is null) return ArchiveOutcome.NotFound;
        if (seen.IsDocument) return ArchiveOutcome.Document;
        if (seen.IsArchived == archived) return ArchiveOutcome.Unchanged;
        return ArchiveOutcome.LevelProfile;
    }

    public Task<bool> AllowsAsync(Guid id, CancellationToken ct = default) =>
        db.DomainObjects.AsNoTracking().AnyAsync(
            o => o.Id == id && o.Facet == null && o.ArchivedAt == null && !ProfileIds().Contains(o.Id), ct);

    public async Task<IReadOnlyList<Guid>> RestoreAsync(
        IReadOnlyDictionary<Guid, DateTimeOffset?> states, CancellationToken ct = default)
    {
        var live = states.Where(s => s.Value is null).Select(s => s.Key).ToList();
        if (live.Count > 0)
            await db.DomainObjects
                .Where(o => live.Contains(o.Id) && o.ArchivedAt != null)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.ArchivedAt, (DateTimeOffset?)null), ct);

        var wanted = states.Where(s => s.Value is not null).Select(s => s.Key).ToList();
        if (wanted.Count == 0) return [];

        // Профили уровня копия в архив не возвращает: запись могла стать профилем уже после снятия
        // копии, и архивный профиль — состояние, которого действие не допускает.
        var profiles = await ProfileIds().Where(id => wanted.Contains(id)).Distinct().ToListAsync(ct);
        var ids = wanted.Except(profiles).ToArray();
        // Дата — в UTC: база принимает метку времени только со смещением 0, а манифест мог быть
        // собран или поправлен снаружи. Иначе одна дата «+03:00» роняла бы восстановление целиком.
        var dates = ids.Select(id => states[id]!.Value.ToUniversalTime()).ToArray();

        // Одним запросом на все записи: после большой чистки справочника архивных — тысячи, и
        // обновление по записи означало бы столько же обращений к базе внутри восстановления.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE domain_objects o
               SET "ArchivedAt" = v.at
              FROM unnest({ids}, {dates}) AS v(id, at)
             WHERE o."Id" = v.id
               AND o."ArchivedAt" IS DISTINCT FROM v.at
               AND NOT EXISTS (SELECT 1 FROM document_facets f WHERE f."ObjectId" = o."Id")
            """, ct);
        return profiles;
    }

    public Task<int> ClearOnDocumentsAsync(CancellationToken ct = default) =>
        db.DomainObjects
            .Where(o => o.Facet != null && o.ArchivedAt != null)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.ArchivedAt, (DateTimeOffset?)null), ct);

    /// <summary>Записи, на которые как на профиль указывает стройка, раздел или комплект.</summary>
    private IQueryable<Guid> ProfileIds() =>
        db.Constructions.Where(c => c.ProfileObjectId != null).Select(c => c.ProfileObjectId!.Value)
            .Concat(db.Sections.Where(s => s.ProfileObjectId != null).Select(s => s.ProfileObjectId!.Value))
            .Concat(db.DocumentSets.Where(s => s.ProfileObjectId != null).Select(s => s.ProfileObjectId!.Value));
}
