using System.Globalization;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Access;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Crew;

public sealed class CrewCandidates(WorkDbContext db, IModuleEmployees employees)
{
    private sealed record Candidate(Guid Id, string FullName);

    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/work/constructions/{id:guid}/crew-candidates", GetAsync).WithTags("Отчёты");

    private static async Task<IResult> GetAsync(
        Guid id, DateOnly date, IModuleUser user, IModuleEmployees employees, ManagerSites managers,
        CrewCandidates candidates, CancellationToken ct)
    {
        var (me, _) = await WorkAccounts.OfUserAsync(user, employees, ct);
        if (me is not { Archived: false } ||
            (!await candidates.IsInstallerOnAsync(me, id, date, ct) &&
             !(await managers.OfEmployeeAsync(me, ct: ct)).Contains(id)))
            return Results.Ok(new { items = Array.Empty<Candidate>(), reason = "not-assigned" });

        var assigned = await candidates.AssignedAsync(id, date, ct);
        var items = (await employees.GetAsync(assigned, ct) ?? [])
            .OrderBy(e => e.FullName, StringComparer.CurrentCulture).ThenBy(e => e.Id)
            .Select(e => new Candidate(e.Id, e.FullName)).ToArray();
        return Results.Ok(new { items, reason = (string?)null });
    }

    internal async Task EnsureAssignedAsync(ReportHeaderValues header, IReadOnlyList<ReportLineValues> lines, CancellationToken ct)
    {
        if (!lines.Any(l => l.Participants.Count > 0)) return;
        var assigned = (await AssignedAsync(header.ConstructionId, header.Date, ct)).ToHashSet();
        for (var i = 0; i < lines.Count; i++)
        {
            var unassigned = lines[i].Participants.Where(id => !assigned.Contains(id)).Distinct().ToArray();
            if (unassigned.Length == 0) continue;
            var people = (await employees.GetAsync(unassigned, ct) ?? []).ToDictionary(e => e.Id, e => e.FullName);
            var names = string.Join(", ", unassigned.Select(id => people.GetValueOrDefault(id) ?? id.ToString()));
            var date = header.Date.ToString("dd.MM", CultureInfo.InvariantCulture);
            throw new InvalidRequestException($"Строка {i + 1}: {names} — нет назначения на стройку {date}.");
        }
    }

    private async Task<bool> IsInstallerOnAsync(ModuleEmployee employee, Guid site, DateOnly date, CancellationToken ct) =>
        employee.NotWorkingOn(date) is null &&
        await AssignmentsOn(site, date).AnyAsync(a => a.Kind == AssignmentKind.Installer && a.EmployeeId == employee.Id, ct);

    private Task<Guid[]> AssignedAsync(Guid site, DateOnly date, CancellationToken ct) =>
        AssignmentsOn(site, date).Select(a => a.EmployeeId).Distinct().ToArrayAsync(ct);

    private IQueryable<Assignment> AssignmentsOn(Guid site, DateOnly date) =>
        db.Assignments.AsNoTracking()
            .Where(a => a.ConstructionId == site && a.DateFrom <= date && (a.DateTo == null || date <= a.DateTo));
}
