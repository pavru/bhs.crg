using BHS.CRG.Modules.Work.Data;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Tests.Work;

internal static class WorkMigrationRows
{
    // Предыдущая схема ещё не знает crew_role. Сеем строки её колонками, независимо от текущей EF-модели.
    public static async Task<(Guid Report, Guid Line)> AddAsync(WorkDbContext db, Guid site, Guid workType,
        string status, bool isCrew, string shift = "day")
    {
        var report = Guid.NewGuid();
        var line = Guid.NewGuid();
        var date = new DateOnly(2026, 11, 16);
        var now = DateTimeOffset.UtcNow;
        const string details = "{}";
        DateTimeOffset? submitted = status == "draft" ? null : now;
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO work.reports (id, employee_id, construction_id, date, shift, status, note, seq,
                created_at, updated_at, window_ends_at, submitted_at)
            VALUES ({report}, {Guid.NewGuid()}, {site}, {date}, {shift}, {status}, 'прежний отчёт',
                nextval('work.report_seq'), {now}, {now}, {now.AddHours(1)}, {submitted})
            """);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO work.report_lines (id, report_id, position, work_type_id, volume, unit_id, is_crew, work_plan_item_id, details)
            VALUES ({line}, {report}, 0, {workType}, 1, {Guid.NewGuid()}, {isCrew}, {Guid.NewGuid()}, CAST({details} AS jsonb))
            """);
        return (report, line);
    }
}
