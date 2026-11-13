using BHS.CRG.Application.Recent;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Api.Endpoints.Account;

/// <summary>
/// Запись отметок «Недавних» (ТЗ CORE-25.3) — вся в одном месте: отметка открытия с вытеснением для
/// адреса <c>POST /api/account/recent</c> и перенос отметок для кода вне адресов (<see cref="IRecentRecords" />).
/// </summary>
public sealed class RecentRecords(AppDbContext db) : IRecentRecords
{
    /// <summary>
    /// Вставить или поднять — и вытеснить лишнее, одной командой. Две вкладки, открывшие одну запись разом,
    /// не упираются в ключ: «вставить» без «поднять» отвечало бы второй отказом.
    /// </summary>
    public Task TouchAsync(Guid userId, string recordType, Guid id, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO user_recent_records ("UserId", "RecordType", "RecordId", "OpenedAt")
            VALUES ({userId}, {recordType}, {id}, {DateTimeOffset.UtcNow})
            ON CONFLICT ("UserId", "RecordType", "RecordId") DO UPDATE SET "OpenedAt" = EXCLUDED."OpenedAt";
            DELETE FROM user_recent_records r
            WHERE r."UserId" = {userId}
              AND (r."RecordType", r."RecordId") NOT IN (
                SELECT k."RecordType", k."RecordId" FROM user_recent_records k
                WHERE k."UserId" = {userId}
                ORDER BY k."OpenedAt" DESC, k."RecordId"
                LIMIT {RecentRecordEndpoints.Limit});
            """, ct);

    /// <summary>
    /// Перенос отслеживаемыми строками, а не командой SQL: так он ложится в базу тем же <c>SaveChanges</c>,
    /// что удаление у вызывающего, — одной транзакцией. Число отметок пользователя не растёт (одна уходит,
    /// одна приходит или сливается), и вытеснять нечего.
    /// </summary>
    public async Task ReassignAsync(string recordType, Guid from, Guid to, CancellationToken ct)
    {
        if (from == to) return;
        var marks = await db.UserRecentRecords
            .Where(r => r.RecordType == recordType && (r.RecordId == from || r.RecordId == to))
            .ToListAsync(ct);
        foreach (var moved in marks.Where(m => m.RecordId == from))
        {
            db.UserRecentRecords.Remove(moved);
            if (marks.FirstOrDefault(m => m.RecordId == to && m.UserId == moved.UserId) is { } kept)
                kept.Merge(moved);
            else
                db.UserRecentRecords.Add(moved.MovedTo(to));
        }
    }
}
