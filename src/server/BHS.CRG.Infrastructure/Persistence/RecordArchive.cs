using BHS.CRG.Application.Objects;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Persistence;

/// <summary>
/// Признак архива записи справочника — условным обновлением, мимо сохранения сущности
/// (issue #1185). Почему так — <see cref="IRecordArchive" />.
/// </summary>
public class RecordArchive(AppDbContext db) : IRecordArchive
{
    public async Task<bool> SetAsync(Guid id, bool archived, CancellationToken ct = default)
    {
        // Условие — в самом запросе: «уже в архиве» и «это документ» решаются тем же обновлением,
        // что и запись, и гонки между проверкой и изменением нет. UpdatedAt не трогаем: данные
        // записи не менялись, а по этой метке судят о свежести именно их.
        DateTimeOffset? value = archived ? DateTimeOffset.UtcNow : null;
        var changed = await db.DomainObjects
            .Where(o => o.Id == id && o.Facet == null && (o.ArchivedAt != null) != archived)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.ArchivedAt, value), ct);
        return changed > 0;
    }

    public async Task RestoreAsync(IReadOnlyDictionary<Guid, DateTimeOffset?> states, CancellationToken ct = default)
    {
        // Снять архив — одним запросом на все записи; поставить — по записи: у каждой своя дата.
        // Архивных в справочнике единицы, действующих — тысячи.
        var live = states.Where(s => s.Value is null).Select(s => s.Key).ToList();
        if (live.Count > 0)
            await db.DomainObjects
                .Where(o => live.Contains(o.Id) && o.ArchivedAt != null)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.ArchivedAt, (DateTimeOffset?)null), ct);

        foreach (var (id, at) in states.Where(s => s.Value is not null))
            await db.DomainObjects
                .Where(o => o.Id == id && o.Facet == null && o.ArchivedAt != at)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.ArchivedAt, at), ct);
    }
}
