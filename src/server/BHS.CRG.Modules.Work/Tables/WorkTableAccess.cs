using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Tables;
using BHS.CRG.Modules.Work.Access;
using BHS.CRG.Modules.Work.Data;

namespace BHS.CRG.Modules.Work.Tables;

internal sealed record WorkTableAccess(ReportViewer Viewer, Guid? EmployeeId, Guid UserId)
{
    internal ModuleTableOutcome Outcome => Viewer.Reach switch
    {
        { Kind: ModuleReachKind.Sites, Sites.Count: 0 } => new(WorkTables.NotAssigned, Reason: WorkTables.NotAssigned),
        { Kind: ModuleReachKind.Sites } reach when Viewer.EmployeeId.HasValue => new(WorkTables.OwnAndSites, reach.Sites.Count),
        var reach => ModuleTableOutcome.Of(reach),
    };
}

internal sealed class WorkTableReaders(ManagerSites managerSites, IModuleEmployees employees)
{
    internal async Task<WorkTableAccess> ResolveAsync(ModuleReader reader, CancellationToken ct)
    {
        var userId = reader.UserId ?? throw new ConflictException(
            $"Таблицы отчётов читаются только по правам человека ({reader.SystemReason}).");
        var employee = await employees.OfAccountAsync(userId, ct);
        var own = reader.Allows(WorkModule.ReportOwn) ? employee?.Id : null;
        ReportViewer viewer;
        if (reader.Allows(WorkModule.ReportRead) || reader.Allows(WorkModule.SitesAll))
            viewer = ReportViewer.Of(ModuleReach.All);
        else if (reader.Allows(WorkModule.ReportReview))
        {
            var reach = ModuleReach.OfSites(employee is null ? [] : await managerSites.OfEmployeeAsync(employee, ct: ct));
            viewer = own is { } employeeId && reach.Sites.Count == 0
                ? ReportViewer.Own(employeeId)
                : ReportViewer.Of(reach, alsoOwn: own);
        }
        else
            viewer = ReportViewer.Of(ModuleReach.Own, own);
        return new(viewer, employee?.Id, userId);
    }
}
