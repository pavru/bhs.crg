using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Access;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Submission;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Endpoints;

internal sealed class ReportEntrySites(
    WorkDbContext db, IModuleConstructions constructions, IModuleClock clock, ReportReads reads, IModuleEmployees employees)
{
    public Task<bool> HasAssignmentAsync(ModuleEmployee employee, Guid site, DateOnly on, CancellationToken ct) =>
        AssignmentGate.EntryAssignments(db, employee, on).AnyAsync(a => a.ConstructionId == site, ct);

    public async Task<IReadOnlyList<MySiteDto>> ForEmployeeAsync(
        ModuleEmployee employee, ModuleReach reach, DateOnly? date, CancellationToken ct)
    {
        if (employee.Archived) return [];
        var assignments = await db.Assignments.AsNoTracking().Where(a => a.EmployeeId == employee.Id).ToListAsync(ct);
        var sites = await SitesAsync(reach, assignments.Select(a => a.ConstructionId).ToHashSet(), ct);
        var matching = assignments.Where(a => sites.TryGetValue(a.ConstructionId, out var site)
            && Covers(a, employee, date ?? site.Today)).GroupBy(a => a.ConstructionId).ToArray();
        if (matching.Length == 0) return [];

        var now = clock.UtcNow();
        // Вчера на стройке не раньше позавчера по UTC. Ночная смена подбирается для исполнителя отчёта.
        var nights = (await reads.NightReportsAsync(ReportViewer.Own(employee.Id),
            DateOnly.FromDateTime(now.UtcDateTime).AddDays(-2), ct)).ToHashSet();
        return matching.Select(group =>
        {
            var (site, today) = sites[group.Key];
            var pair = SubmissionWindow.Suggested(site.TimeZoneId, now, nights.Contains((site.Id, today.AddDays(-1))));
            return new MySiteDto(site.Id, site.Name,
                group.Select(a => a.Kind).Distinct().Order().Select(Assignment.KindCode).ToArray(),
                today, site.TimeZoneId, new SuggestedDto(pair.Date, Report.ShiftCode(pair.Shift), pair.Label));
        }).OrderBy(s => s.Name, StringComparer.CurrentCulture).ToArray();
    }

    public async Task<IReadOnlyList<ModuleEmployee>?> CandidatesAsync(
        ModuleReach reach, DateOnly? date, string? search, int limit, CancellationToken ct)
    {
        var scoped = db.Assignments.AsNoTracking();
        if (reach.Kind != ModuleReachKind.All)
        {
            var allowed = reach.Sites.ToArray();
            scoped = scoped.Where(a => allowed.Contains(a.ConstructionId));
        }
        if (date is { } day)
            scoped = scoped.Where(a => a.DateFrom <= day && (a.DateTo == null || a.DateTo >= day));
        var assignments = await scoped.ToListAsync(ct);
        var sites = await SitesAsync(reach, assignments.Select(a => a.ConstructionId).ToHashSet(), ct);
        var active = assignments.Where(a => sites.TryGetValue(a.ConstructionId, out var site)
            && a.Covers(date ?? site.Today)).ToLookup(a => a.EmployeeId, a => date ?? sites[a.ConstructionId].Today);
        var people = await employees.GetAsync([.. active.Select(a => a.Key)], ct);
        if (people is null) return null;
        var q = search?.Trim();
        return people.Where(e => !e.Archived && active[e.Id].Any(on => e.NotWorkingOn(on) is null)
                && (string.IsNullOrEmpty(q) || e.FullName.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                    || e.PersonnelNumber?.Contains(q, StringComparison.OrdinalIgnoreCase) == true))
            .OrderBy(e => e.FullName, StringComparer.CurrentCulture).ThenBy(e => e.Id).Take(limit).ToArray();
    }

    private async Task<Dictionary<Guid, (ModuleConstruction Site, DateOnly Today)>> SitesAsync(
        ModuleReach reach, IReadOnlySet<Guid> assigned, CancellationToken ct)
    {
        var result = new Dictionary<Guid, (ModuleConstruction, DateOnly)>();
        foreach (var site in await constructions.ListAsync(ct))
            if (assigned.Contains(site.Id) && reach.Covers(site.Id, own: false)
                && await clock.TodayOnSiteAsync(site.Id, ct) is { } today)
                result.Add(site.Id, (site, today));
        return result;
    }

    private static bool Covers(Assignment assignment, ModuleEmployee employee, DateOnly date) =>
        assignment.Covers(date) && employee.NotWorkingOn(date) is null;
}
