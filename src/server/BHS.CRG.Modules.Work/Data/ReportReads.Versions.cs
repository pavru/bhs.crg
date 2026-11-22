using BHS.CRG.Modules.Work.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Data;

public sealed partial class ReportReads
{
    private IQueryable<ReportVersion> History(ReportViewer viewer, Guid? userId)
    {
        var readable = Visible(viewer).Where(ContentReadable(viewer.EmployeeId, userId));
        return db.Set<ReportVersion>().AsNoTracking()
            .Where(v => readable.Any(r => r.Id == v.RecordId));
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<ReportVersionDto>>> VersionListsAsync(
        ReportViewer viewer, Guid? userId, IReadOnlyList<Report> reports, CancellationToken ct)
    {
        var limits = reports.Where(r => r.CurrentVersion > 1).ToDictionary(r => r.Id, r => r.CurrentVersion);
        if (limits.Count == 0) return new Dictionary<Guid, IReadOnlyList<ReportVersionDto>>();
        var ids = limits.Keys.ToArray();
        var rows = await History(viewer, userId).Where(v => ids.Contains(v.RecordId)).OrderBy(v => v.Number)
            .Select(v => new { v.RecordId, v.Number, v.Reason, v.AuthorId, v.AuthorName, v.At }).ToListAsync(ct);
        return rows.Where(v => v.Number < limits[v.RecordId]).GroupBy(v => v.RecordId)
            .ToDictionary(g => g.Key, IReadOnlyList<ReportVersionDto> (g) =>
                [.. g.Select(v => new ReportVersionDto(v.Number, v.Reason, new(v.AuthorId, v.AuthorName), v.At))]);
    }

    internal Task<ReportVersion?> VersionAtClosingAsync(
        ReportViewer viewer, Guid? userId, Guid reportId, int currentVersion, Guid closingId, CancellationToken ct) =>
        History(viewer, userId).Where(v => v.RecordId == reportId && v.ClosingId == closingId && v.Number < currentVersion)
            .OrderBy(v => v.Number).FirstOrDefaultAsync(ct);

    internal Task<ReportVersion?> VersionAsync(
        ReportViewer viewer, Guid? userId, Guid reportId, int number, CancellationToken ct) =>
        History(viewer, userId).FirstOrDefaultAsync(v => v.RecordId == reportId && v.Number == number, ct);

}
