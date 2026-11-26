using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Crew;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Data;

internal sealed record LaborLine(Guid PlanItemId, Guid? SectionId, Guid WorkTypeId, Guid UnitId, decimal Volume, bool IsCrew);

internal sealed record LaborReport(Guid Id, Guid ConstructionId, Guid EmployeeId, DateOnly Date, ReportShift Shift,
    ReportStatus Status, decimal? Hours, IReadOnlyList<LaborLine> Lines)
{
    public bool Accepted => Status == ReportStatus.Accepted;

    public CrewShiftKey ShiftKey => new(ConstructionId, Date, Shift);
}

internal sealed record LaborInputs(
    IReadOnlyList<LaborReport> Reports, IReadOnlyList<CrewGroupRecord> Groups, IReadOnlyList<CrewGroupLine> CrewLines,
    IReadOnlyList<CrewSubmittedEmployee> SubmittedEmployees);

public sealed partial class ReportReads
{
    // Отбор отчётов по виду берёт все строки каждого кандидата: именно по ним определяется смешанный отчёт.
    // Содержимое черновика не читается. При сравнении используются часы только принятых отчётов.
    internal async Task<LaborInputs> LaborAsync(ReportViewer viewer, DateOnly? from, DateOnly? to,
        Guid? workTypeId, CancellationToken ct)
    {
        var reports = Visible(viewer).AsNoTracking().Where(r => r.SubmittedAt != null);
        var groups = CrewGroups(viewer.Reach).AsNoTracking();
        if (from is { } start)
        {
            reports = reports.Where(r => r.Date >= start);
            groups = groups.Where(g => g.Date >= start);
        }
        if (to is { } end)
        {
            reports = reports.Where(r => r.Date <= end);
            groups = groups.Where(g => g.Date <= end);
        }
        if (workTypeId is { } kind)
        {
            reports = reports.Where(r => r.Lines.Any(l => l.WorkTypeId == kind));
            groups = groups.Where(g => g.WorkTypeId == kind);
        }

        var projected = await reports.Select(r => new LaborReport(r.Id, r.ConstructionId, r.EmployeeId, r.Date,
            r.Shift, r.Status, r.Status == ReportStatus.Accepted ? r.Hours : null,
            r.Lines.Select(l => new LaborLine(l.WorkPlanItemId, l.SectionId, l.WorkTypeId, l.UnitId, l.Volume, l.IsCrew))
                .ToList())).ToListAsync(ct);
        var crew = await groups.ToListAsync(ct);
        if (crew.Count == 0) return new(projected, [], [], []);
        var lines = await CrewLinesAsync(viewer, groups, ct);
        var submitted = await CrewSubmittedEmployeesAsync(viewer, groups, lines, ct);
        return new(projected, crew, lines, submitted);
    }
}
