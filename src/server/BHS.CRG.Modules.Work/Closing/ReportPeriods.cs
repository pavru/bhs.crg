using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;

namespace BHS.CRG.Modules.Work.Closing;

public sealed class ReportPeriods(WorkDbContext db, IModulePeriods periods)
{
    public async Task<PeriodBoundaries> UnderWriteLockAsync(CancellationToken ct)
    {
        await OpenPeriodWrite.HoldAsync(db, ct);
        return await periods.BoundariesAsync(ct);
    }

    public Task<PeriodBoundaries> CurrentAsync(CancellationToken ct) => periods.BoundariesAsync(ct);

    public Task<(Guid Id, DateTimeOffset At)?> ClosingAsync(Report report, CancellationToken ct) =>
        periods.ClosingAsync(report.ConstructionId, report.Date, ct);

    public static bool IsClosed(PeriodBoundaries boundaries, Guid site, DateOnly date) =>
        boundaries.IsClosed(date, new PeriodContour.Construction(site));

    public static void EnsureOpen(PeriodBoundaries boundaries, Report report) =>
        EnsureOpen(boundaries, report.ConstructionId, report.Date, report.Id);

    public static void EnsureOpen(PeriodBoundaries boundaries, Guid site, DateOnly date, Guid? reportId = null)
    {
        if (!IsClosed(boundaries, site, date)) return;

        throw new ConflictException(reportId is { } id
            ? $"Период закрыт: исправление — новой версией (POST /api/work/reports/{id}/correction)."
            : "Период закрыт: новый отчёт на эту дату завести нельзя.");
    }
}
