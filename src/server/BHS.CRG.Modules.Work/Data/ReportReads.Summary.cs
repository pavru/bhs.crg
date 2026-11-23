using BHS.CRG.Modules.Work.Summary;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Data;

public sealed partial class ReportReads
{
    internal Task<List<SummaryReport>> SummaryAsync(
        ReportViewer viewer, DateOnly from, DateOnly to, Guid[] constructionIds, CancellationToken ct) =>
        SummaryReports(viewer, from, to, constructionIds)
            .Select(r => new SummaryReport(r.EmployeeId, r.ConstructionId, r.Date, r.Shift, r.Status,
                r.Status == ReportStatus.Draft ? null : (Guid?)r.Id,
                r.Status == ReportStatus.Draft ? null : r.Hours,
                r.Status == ReportStatus.Draft ? (DateTimeOffset?)r.WindowEndsAt : null))
            .ToListAsync(ct);

    internal Task<Guid[]> SummaryOwnSitesAsync(
        ReportViewer viewer, DateOnly from, DateOnly to, Guid[] outsideSiteIds, CancellationToken ct) =>
        SummaryReports(viewer, from, to, outsideSiteIds).Where(r => r.EmployeeId == viewer.EmployeeId)
            .Select(r => r.ConstructionId).Distinct().ToArrayAsync(ct);

    private IQueryable<Report> SummaryReports(ReportViewer viewer, DateOnly from, DateOnly to, Guid[] constructionIds) =>
        Visible(viewer).AsNoTracking()
            .Where(r => constructionIds.Contains(r.ConstructionId) && r.Date >= from && r.Date <= to);
}
