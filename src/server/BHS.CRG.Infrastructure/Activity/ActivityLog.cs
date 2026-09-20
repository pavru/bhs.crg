using BHS.CRG.Application.Activity;
using BHS.CRG.Domain.Activity;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Activity;

/// <summary>
/// Единственное место, где к набору записей журнала обращаются напрямую (issue #950).
///
/// ⚠️ Это не соглашение об оформлении: сторож <c>ActivityLogInventoryTests</c> перечисляет все
/// упоминания набора в исходниках и падает, если их стало больше. Проверки на уровне сущности
/// мало — прямой запрос к базе обходит и приватные сеттеры, и фабрику.
/// </summary>
public sealed class ActivityLog(AppDbContext db, IActivityActor actor) : IActivityLog
{
    /// <summary>Потолок страницы: журнал читают экраном, а не выгрузкой, и «take=100000» превратило
    /// бы чтение в способ выгрузить его целиком одним запросом.</summary>
    private const int MaxTake = 200;

    public async Task RecordAsync(ActivityAction action, string? targetId = null, string? targetLabel = null,
        string? before = null, string? after = null, CancellationToken ct = default)
    {
        var who = actor.Current;
        db.ActivityRecords.Add(ActivityRecord.Create(
            action.Code, who.Id, who.Name, targetId, targetLabel, before, after));
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ActivityRecord>> ReadAsync(int skip, int take, string? action = null,
        CancellationToken ct = default) =>
        await Filtered(action)
            .OrderByDescending(r => r.OccurredAt)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, MaxTake))
            .AsNoTracking()
            .ToListAsync(ct);

    public Task<int> CountAsync(string? action = null, CancellationToken ct = default) =>
        Filtered(action).CountAsync(ct);

    public Task<ActivityRecord?> LastAsync(ActivityAction action, CancellationToken ct = default) =>
        db.ActivityRecords.Where(r => r.Action == action.Code)
            .OrderByDescending(r => r.OccurredAt)
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<ActivityRecord>> ExportAsync(CancellationToken ct = default) =>
        await db.ActivityRecords.OrderBy(r => r.OccurredAt).AsNoTracking().ToListAsync(ct);

    public async Task<int> ImportAsync(IReadOnlyList<ActivityRecord> records, CancellationToken ct = default)
    {
        if (records.Count == 0) return 0;

        var known = await db.ActivityRecords
            .Where(r => records.Select(x => x.Id).Contains(r.Id))
            .Select(r => r.Id)
            .ToHashSetAsync(ct);

        var fresh = records.Where(r => !known.Contains(r.Id)).ToList();
        if (fresh.Count == 0) return 0;

        db.ActivityRecords.AddRange(fresh);
        await db.SaveChangesAsync(ct);
        return fresh.Count;
    }

    private IQueryable<ActivityRecord> Filtered(string? action) =>
        string.IsNullOrWhiteSpace(action)
            ? db.ActivityRecords
            : db.ActivityRecords.Where(r => r.Action == action);
}
