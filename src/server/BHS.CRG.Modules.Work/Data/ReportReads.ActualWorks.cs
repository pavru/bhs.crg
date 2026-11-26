using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Crew;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Data;

internal sealed record AcceptedWorkLine(
    Guid PlanItemId, DateOnly Date, ReportShift Shift, Guid? SectionId, Guid WorkTypeId, Guid UnitId,
    decimal Volume, bool IsCrew);

internal sealed record ActualWorksInputs(
    int AwaitingAcceptance, IReadOnlyList<AcceptedWorkLine> AcceptedLines, IReadOnlyList<CrewGroupRecord> Groups,
    IReadOnlyList<CrewGroupLine> CrewLines, IReadOnlyList<CrewSubmittedEmployee> SubmittedEmployees);

public sealed partial class ReportReads
{
    /// <summary>Входы для факта без людей (WORK-39). Охват ограничен стройкой и датами, назначения не участвуют.
    /// Вызывается поставщиком внутри одного снимка; в контракт попадают только объёмы и счётчики.</summary>
    internal async Task<ActualWorksInputs> ActualWorksAsync(ModuleReader reader, ActualWorksQuery query, CancellationToken ct)
    {
        if (reader.IsSystem || !reader.Allows(WorkModule.FactsRead))
            throw new ForbiddenException("Чтение фактических работ требует права work.facts.read.");

        var reach = ModuleReach.OfSites([query.ConstructionId]);
        var viewer = ReportViewer.Of(reach);
        var reports = db.Set<Report>().AsNoTracking().Where(r => r.ConstructionId == query.ConstructionId);
        var groups = CrewGroups(reach).AsNoTracking();
        if (query.From is { } from)
        {
            reports = reports.Where(r => r.Date >= from);
            groups = groups.Where(g => g.Date >= from);
        }
        if (query.To is { } to)
        {
            reports = reports.Where(r => r.Date <= to);
            groups = groups.Where(g => g.Date <= to);
        }

        var awaiting = await reports.CountAsync(r => r.Status == ReportStatus.Submitted, ct);
        var accepted = await reports.Where(r => r.Status == ReportStatus.Accepted)
            .SelectMany(r => r.Lines.Select(l => new AcceptedWorkLine(l.WorkPlanItemId, r.Date, r.Shift,
                l.SectionId, l.WorkTypeId, l.UnitId, l.Volume, l.IsCrew))).ToListAsync(ct);
        var crew = await groups.ToListAsync(ct);
        if (crew.Count == 0) return new(awaiting, accepted, [], [], []);
        var lines = await CrewLinesAsync(viewer, groups, ct);
        var submitted = await CrewSubmittedEmployeesAsync(viewer, groups, lines, ct);
        return new(awaiting, accepted, crew, lines, submitted);
    }
}
