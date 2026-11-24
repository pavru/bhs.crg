using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Tables;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Tables;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Summary;

internal sealed record SummaryConstruction(Guid Id, string Name);

internal sealed record SummaryPage(
    IReadOnlyList<DateOnly> Days, IReadOnlyList<SummaryRow> Rows, IReadOnlyList<SummaryConstruction> Constructions,
    string Boundary, string BoundaryOutcome, string? Reason, int Total, int Offset, int Limit);

internal sealed class SummaryQuery(
    WorkDbContext db, ReportReads reads, WorkTableReaders readers, IModuleConstructions constructions,
    IModuleEmployees employees, IModuleClock clock)
{
    internal async Task<SummaryPage> ReadAsync(
        ModuleReader reader, DateOnly[] days, Guid? filter, int offset, int limit, CancellationToken ct)
    {
        var access = await readers.ResolveAsync(reader, ct);
        var viewer = access.Viewer;
        var now = clock.UtcNow();
        var known = await constructions.ListAsync(ct);
        var managed = known.Where(site => viewer.Reach.Covers(site.Id, own: false)).Select(site => site.Id).ToHashSet();
        var from = days[0];
        var to = days[^1];
        var own = viewer.EmployeeId;
        var outside = own is null ? [] : known.Select(site => site.Id).Where(id => !managed.Contains(id)).ToArray();
        var ownSites = outside.Length == 0 ? [] : await reads.SummaryOwnSitesAsync(viewer, from, to, outside, ct);
        var available = known.Where(site => managed.Contains(site.Id) || ownSites.Contains(site.Id)).ToArray();
        if (filter is { } siteId && !available.Any(site => site.Id == siteId))
            throw new NotFoundException("Стройка не найдена.");

        var wanted = available.Where(site => filter is null || filter == site.Id).Select(site => site.Id).ToArray();
        var reports = wanted.Length == 0 ? [] : await reads.SummaryAsync(viewer, from, to, wanted, ct);
        var managedIds = wanted.Where(managed.Contains).ToArray();
        // За пределами управляемых строек назначения объясняют только строки собственных отчётов.
        var ownSiteIds = reports.Select(r => r.ConstructionId).Where(id => !managed.Contains(id)).Distinct().ToArray();
        var assignments = await db.Assignments.AsNoTracking()
            .Where(a => a.Kind == AssignmentKind.Installer && a.DateFrom <= to && a.DateTo >= from)
            .Where(a => managedIds.Contains(a.ConstructionId)
                || (a.EmployeeId == own && ownSiteIds.Contains(a.ConstructionId)))
            .ToListAsync(ct);

        var pairs = reports.Select(r => (r.EmployeeId, r.ConstructionId))
            .Union(assignments.Select(a => (a.EmployeeId, a.ConstructionId))).ToArray();
        var people = pairs.Length == 0 ? [] : await employees.GetAsync([.. pairs.Select(p => p.EmployeeId).Distinct()], ct) ?? [];
        var personById = people.ToDictionary(p => p.Id);
        var rowSites = pairs.Select(p => p.ConstructionId).ToHashSet();
        var siteById = new Dictionary<Guid, SummarySite>();
        foreach (var site in known.Where(site => rowSites.Contains(site.Id)))
        {
            if (await clock.SiteDateOfAsync(site.Id, now, ct) is { } today)
                siteById.Add(site.Id, new(site.Id, site.Name, site.TimeZoneId, today));
        }

        var periods = assignments.ToLookup(a => (a.EmployeeId, a.ConstructionId));
        var reportRows = reports.ToLookup(r => (r.EmployeeId, r.ConstructionId));
        var visiblePairs = pairs.Where(p => siteById.ContainsKey(p.ConstructionId))
            .OrderBy(p => personById.GetValueOrDefault(p.EmployeeId)?.FullName ?? SummaryLayout.UnknownEmployee,
                StringComparer.CurrentCulture).ThenBy(p => p.EmployeeId)
            .ThenBy(p => siteById[p.ConstructionId].Name, StringComparer.CurrentCulture).ThenBy(p => p.ConstructionId)
            .ToArray();
        var rows = visiblePairs.Skip(offset).Take(limit).Select(p =>
            SummaryLayout.Row(p.EmployeeId, personById.GetValueOrDefault(p.EmployeeId), siteById[p.ConstructionId],
                days, periods[p], reportRows[p], now)).ToArray();
        var choices = available.Select(s => new SummaryConstruction(s.Id, s.Name)).ToArray();
        var outcome = access.Outcome;
        return new(days, rows, choices, Boundary(outcome), outcome.Code, outcome.Reason, visiblePairs.Length, offset, limit);
    }

    private static string Boundary(ModuleTableOutcome outcome) => outcome.Code switch
    {
        "all" => "все стройки",
        "own" => "ваши отчёты",
        WorkTables.OwnAndSites => $"ваши отчёты и отчёты ваших строек ({outcome.Sites})",
        WorkTables.NotAssigned => "отчёты ваших строек (0)",
        _ => $"отчёты ваших строек ({outcome.Sites})",
    };
}
