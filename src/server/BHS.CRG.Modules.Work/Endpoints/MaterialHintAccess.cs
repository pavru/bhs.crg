using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Access;

namespace BHS.CRG.Modules.Work.Endpoints;

internal sealed class MaterialHintAccess(
    IModuleReaders readers, IModuleClock clock, IModuleEmployees employees, ManagerSites managers, ReportEntrySites entry)
{
    public async Task<ModuleReader> ForSiteAsync(Guid constructionId, DateOnly? date, CancellationToken ct)
    {
        var reader = await readers.ForRequestAsync(ct);
        if (await clock.TodayOnSiteAsync(constructionId, ct) is not { } today || reader.UserId is not { } userId)
            throw MissingSite();

        if (reader.Allows(WorkModule.ReportRead) || reader.Allows(WorkModule.SitesAll)) return reader;
        if (reader.Allows(WorkModule.ReportReview) && (await managers.ReachAsync(reader, ct)).Covers(constructionId, own: false))
            return reader;
        if (reader.Allows(WorkModule.ReportOwn) && await employees.OfAccountAsync(userId, ct) is { } employee
            && await entry.HasAssignmentAsync(employee, constructionId, date ?? today, ct))
            return reader;

        throw MissingSite();
    }

    private static NotFoundException MissingSite() => new("Стройка не найдена среди доступных вам строек.");
}
