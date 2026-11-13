using BHS.CRG.Application.Recent;
using BHS.CRG.Domain.Templates;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Api.Endpoints.Account;

/// <summary>
/// Запись отметок «Недавних» (ТЗ CORE-25.3) — вся в одном месте: отметка открытия с вытеснением для
/// адреса <c>POST /api/account/recent</c> и уход версии шаблона для кода вне адресов (<see cref="IRecentRecords" />).
/// </summary>
public sealed class RecentRecords(AppDbContext db) : IRecentRecords
{
    /// <summary>
    /// Вставить или поднять — и вытеснить лишнее, одной командой. Две вкладки, открывшие одну запись разом,
    /// не упираются в ключ: «вставить» без «поднять» отвечало бы второй отказом.
    ///
    /// <para><b>Шаблон — одна отметка на все версии</b> (shell-09, Р-5): открытие версии снимает отметки
    /// остальных версий того же шаблона у этого пользователя. Иначе каждая новая версия занимала бы своё место
    /// из <see cref="RecentRecordEndpoints.Limit" /> и вытесняла бы чужие записи, хотя на Главной все версии —
    /// одна строка.</para>
    /// </summary>
    public async Task TouchAsync(Guid userId, string recordType, Guid id, CancellationToken ct)
    {
        if (recordType == IdRecordTypes.Template)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM user_recent_records r
                WHERE r."UserId" = {userId} AND r."RecordType" = {recordType} AND r."RecordId" <> {id}
                  AND r."RecordId" IN (
                    SELECT o."Id" FROM templates o
                    JOIN templates s ON s."DocumentTypeId" = o."DocumentTypeId" AND s."Name" = o."Name"
                    WHERE s."Id" = {id});
                """, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
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
    }

    /// <summary>
    /// Перенос — командой «вставить или поднять», а не отслеживаемыми строками: строка, которую другая
    /// вкладка вставит между чтением и сохранением, упёрлась бы в ключ и уронила бы удаление версии. Версии
    /// читаются без содержимого — для выбора текущей хватает признаков.
    /// </summary>
    public async Task TemplateVersionLeavingAsync(Guid versionId, CancellationToken ct)
    {
        var leaving = await db.Templates.AsNoTracking().Where(t => t.Id == versionId)
            .Select(t => new { t.DocumentTypeId, t.Name }).SingleOrDefaultAsync(ct);
        if (leaving is null) return;
        var rest = await db.Templates.AsNoTracking()
            .Where(v => v.DocumentTypeId == leaving.DocumentTypeId && v.Name == leaving.Name && v.Id != versionId)
            .Select(v => new TemplateVersionFacts(v.Id, v.IsActive, v.IsDefault, v.Version, v.UpdatedAt))
            .ToListAsync(ct);

        const string type = IdRecordTypes.Template;
        if (TemplateCurrentVersion.Of(rest) is { } current)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO user_recent_records ("UserId", "RecordType", "RecordId", "OpenedAt")
                SELECT r."UserId", r."RecordType", {current.Id}, r."OpenedAt" FROM user_recent_records r
                WHERE r."RecordType" = {type} AND r."RecordId" = {versionId}
                ON CONFLICT ("UserId", "RecordType", "RecordId")
                  DO UPDATE SET "OpenedAt" = GREATEST(user_recent_records."OpenedAt", EXCLUDED."OpenedAt");
                """, ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""DELETE FROM user_recent_records WHERE "RecordType" = {type} AND "RecordId" = {versionId};""", ct);
    }
}
