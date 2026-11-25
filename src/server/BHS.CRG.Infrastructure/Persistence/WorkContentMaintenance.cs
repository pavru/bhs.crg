using System.Text.RegularExpressions;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Persistence;

/// <summary>Адресное обслуживание прежних записей Work при старте и импорте копии.</summary>
public static class WorkContentMaintenance
{
    private const string Returned = "work.report.returned";
    private const string Corrected = "work.report.corrected";
    private const string ReturnedState = "возвращён";
    private const string CorrectedState = "исправлен";
    private const string LegacyMessage = "Комментарий к возврату доступен в журнале отчёта.";
    private const string LegacyTitle = @"^Отчёт за (0[1-9]|[12][0-9]|3[01])\.(0[1-9]|1[0-2]) возвращён$";

    public static async Task AtStartupAsync(AppDbContext db, ILogger logger, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var counts = await ApplyAsync(db, ct);
        await transaction.CommitAsync(ct);
        counts.Log(logger);
    }

    internal static Task<CleanupCounts> DuringRestoreAsync(AppDbContext db, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Для обслуживания данных Work нужна транзакция восстановления.");
        return ApplyAsync(db, ct);
    }

    private static async Task<CleanupCounts> ApplyAsync(AppDbContext db, CancellationToken ct)
    {
        var sources = await CleanSourcesAsync(db, ct);
        // Единственные действия, которые сохраняли свободный текст в After. Before и метаданные
        // события остаются на месте. Обычная запись журнала защищена в AppDbContext.SaveChanges.
        var audit = await db.ActivityRecords.Where(a => a.After != null && (
                (a.Action == Returned && a.After != ReturnedState) ||
                (a.Action == Corrected && a.After != CorrectedState)))
            .ExecuteUpdateAsync(set => set
                .SetProperty(a => a.After, a => a.Action == Returned ? ReturnedState : CorrectedState), ct);

        // Этот полный заголовок принадлежит издателю возврата. До появления ссылки он создавал
        // только личное предупреждение без источника, аудитории и подписи ссылки. Из текста ничего
        // не извлекаем: разделители могли входить и в название стройки, и в комментарий.
        var notices = await db.Notifications.Where(n => n.Severity == NotificationSeverity.Warning
                && n.UserId != null && n.Source == null && n.Audience == null
                && n.LinkUrl == null && n.LinkLabel == null && n.Message != LegacyMessage
                && Regex.IsMatch(n.Title, LegacyTitle))
            .ExecuteUpdateAsync(set => set.SetProperty(n => n.Message, LegacyMessage), ct);
        return new CleanupCounts(sources, audit, notices);
    }

    private static Task<int> CleanSourcesAsync(AppDbContext db, CancellationToken ct)
    {
        var reports = SystemDataSets.TableMarkerPrefix + "work.reports";
        var lines = SystemDataSets.TableMarkerPrefix + "work.lines";
        var reportsWithColumns = reports + SystemDataSets.TableColumnsParameter + "%";
        var linesWithColumns = lines + SystemDataSets.TableColumnsParameter + "%";
        var neutralCount = DataSetSource.UnstoredRowCount;
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH work_sources AS (
                SELECT s."Id", COALESCE((
                    SELECT jsonb_agg(
                        CASE WHEN jsonb_typeof(column_info) = 'object' AND column_info ? 'sampleValues'
                             THEN jsonb_set(column_info, ARRAY['sampleValues'], '[]'::jsonb)
                             ELSE column_info END
                        ORDER BY position)
                    FROM jsonb_array_elements(
                        CASE WHEN jsonb_typeof(s."CachedSchema") = 'array' THEN s."CachedSchema"
                             ELSE '[]'::jsonb END)
                        WITH ORDINALITY AS columns(column_info, position)
                ), '[]'::jsonb) AS schema
                FROM dataset_sources s
                JOIN dataset_files f ON f."Id" = s."FileId"
                WHERE f."Format" = {nameof(DataSetFormat.System)}
                  AND (s."SheetOrPath" ILIKE {reports} OR s."SheetOrPath" ILIKE {reportsWithColumns}
                    OR s."SheetOrPath" ILIKE {lines} OR s."SheetOrPath" ILIKE {linesWithColumns})
            )
            UPDATE dataset_sources s
            SET "CachedSchema" = w.schema, "CachedData" = NULL, "CachedRowCount" = {neutralCount}
            FROM work_sources w
            WHERE s."Id" = w."Id"
              AND (s."CachedSchema" IS DISTINCT FROM w.schema OR s."CachedData" IS NOT NULL
                   OR s."CachedRowCount" <> {neutralCount})
            """, ct);
    }

    internal readonly record struct CleanupCounts(int Sources, int Audit, int Notices)
    {
        public void Log(ILogger logger)
        {
            if (Sources + Audit + Notices == 0) return;
            logger.LogInformation("Обслуживание Work v1: источники {Sources}, аудит {Audit}, уведомления {Notices}",
                Sources, Audit, Notices);
        }
    }
}
