using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Tables;
using BHS.CRG.Modules.Work.Access;
using BHS.CRG.Modules.Work.Data;

namespace BHS.CRG.Modules.Work.Tables;

internal sealed record WorkTableAccess(ReportViewer Viewer, Guid? EmployeeId, Guid UserId)
{
    internal ModuleTableOutcome Outcome => Viewer.Reach is { Kind: ModuleReachKind.Sites, Sites.Count: 0 }
        ? new(WorkTables.NotAssigned, Reason: WorkTables.NotAssigned)
        : ModuleTableOutcome.Of(Viewer.Reach);
}

internal sealed class WorkTableReaders(ManagerSites managerSites, IModuleEmployees employees)
{
    internal async Task<WorkTableAccess> ResolveAsync(ModuleReader reader, CancellationToken ct)
    {
        var userId = reader.UserId ?? throw new ConflictException(
            $"Таблицы отчётов читаются только по правам человека ({reader.SystemReason}).");
        var employee = await employees.OfAccountAsync(userId, ct);
        ReportViewer viewer;
        if (reader.Allows(WorkModule.ReportRead) || reader.Allows(WorkModule.SitesAll))
            viewer = ReportViewer.Of(ModuleReach.All);
        else if (reader.Allows(WorkModule.ReportReview))
            viewer = ReportViewer.Of(ModuleReach.OfSites(employee is null ? [] : await managerSites.OfEmployeeAsync(employee, ct: ct)));
        else if (reader.Allows(WorkModule.ReportOwn) && employee is not null)
            viewer = ReportViewer.Own(employee.Id);
        else
            viewer = ReportViewer.Of(ModuleReach.Own);
        return new(viewer, employee?.Id, userId);
    }
}
