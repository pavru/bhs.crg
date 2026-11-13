using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Access;
using BHS.CRG.Modules.Work.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Endpoints;

/// <summary>Тело массового «назначить на период».</summary>
public sealed record AssignmentBulkRequest(
    Guid? ConstructionId, string? Kind, DateOnly? DateFrom, DateOnly? DateTo, IReadOnlyList<Guid>? EmployeeIds);

/// <summary>Кандидат массового назначения: годен ли, а если нет — почему, словами (D15 зонтика).</summary>
/// <param name="Assigned">Пересекающееся назначение того же вида на этой стройке, если уже есть.</param>
public sealed record AssignmentCandidateDto(
    Guid EmployeeId, string Name, string? PersonnelNumber, bool Eligible, string? Reason, AssignedPeriod? Assigned);

/// <summary>Период уже заведённого назначения у кандидата.</summary>
public sealed record AssignedPeriod(Guid Id, DateOnly DateFrom, DateOnly? DateTo);

/// <summary>
/// Массовое «назначить на период» (ТЗ WORK-21; change <c>work-11-assignments</c>): кандидаты с причиной исключения
/// и заведение пачкой «всё или ничего». Причины и пересечения считает сервер теми же правилами
/// (<see cref="AssignmentRules" />), что и одиночное заведение: кандидат «годен» ровно тогда, когда пачка его
/// не отвергнет.
/// </summary>
internal static class AssignmentBulkEndpoints
{
    /// <summary>Сколько сотрудников в пачке и в выдаче кандидатов — бригада, а не весь штат.</summary>
    public const int Max = 200;

    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/candidates", CandidatesAsync);
        group.MapPost("/bulk", BulkAsync);
    }

    private static async Task<IResult> CandidatesAsync(
        Guid? constructionId, string? kind, DateOnly? dateFrom, DateOnly? dateTo, string? q,
        WorkDbContext db, IModuleConstructions constructions, IModuleEmployees employees, CancellationToken ct)
    {
        var siteId = constructionId ?? throw new InvalidRequestException("Не названа стройка (constructionId).");
        var parsed = Assignment.ParseKind(kind);
        var (from, to) = AssignmentRules.Period(parsed, dateFrom, dateTo);
        await AssignmentEndpoints.SiteAsync(constructions, siteId, ct);

        if (await employees.SearchAsync(q, from, Max, ct) is not { } found) return AssignmentRules.DirectoryMissing();
        var assigned = await OverlapsAsync(db, siteId, parsed, from, to, ct);

        var items = found.Items.Select(e =>
        {
            var existing = assigned.GetValueOrDefault(e.Id);
            var reason = AssignmentRules.Unfit(e, parsed, from, to)
                ?? (existing is null ? null : AssignmentRules.AlreadyAssigned(existing));
            return new AssignmentCandidateDto(e.Id, e.FullName, e.PersonnelNumber, reason is null, reason,
                existing is null ? null : new AssignedPeriod(existing.Id, existing.DateFrom, existing.DateTo));
        }).ToList();
        return Results.Ok(new { items, inArchive = found.InArchive, limit = Max });
    }

    /// <summary>
    /// Пачка «всё или ничего»: хотя бы один негодный — 422 со всеми отказавшими поимённо, и не заведено ни одного
    /// назначения. Половина бригады, назначенная молча, выглядела бы назначенной целиком.
    /// </summary>
    private static async Task<IResult> BulkAsync(
        AssignmentBulkRequest body, WorkDbContext db, AssignmentGate gate, IModuleConstructions constructions,
        IModuleEmployees employees, IModuleClock clock, IModuleActivityLog log, TimeProvider time, CancellationToken ct)
    {
        var siteId = body.ConstructionId ?? throw new InvalidRequestException("Не названа стройка (constructionId).");
        var kind = Assignment.ParseKind(body.Kind);
        var (from, to) = AssignmentRules.Period(kind, body.DateFrom, body.DateTo);
        var ids = (body.EmployeeIds ?? []).Distinct().ToList();
        if (ids.Count == 0) throw new InvalidRequestException("Не назван ни один сотрудник (employeeIds).");
        if (ids.Count > Max) throw new InvalidRequestException($"В пачке не больше {Max} сотрудников, а названо {ids.Count}.");

        var site = await AssignmentEndpoints.SiteAsync(constructions, siteId, ct);
        await gate.EnsureAsync(siteId, ct);
        if (await employees.GetAsync(ids, ct) is not { } found) return AssignmentRules.DirectoryMissing();
        var byId = found.ToDictionary(e => e.Id);

        var now = time.GetUtcNow();
        var created = new List<(Assignment Assignment, ModuleEmployee Employee)>();
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.LockSiteAsync(siteId, ct);
            var assigned = await OverlapsAsync(db, siteId, kind, from, to, ct);
            var refused = new List<AssignmentRules.Refused>();
            foreach (var id in ids)
            {
                if (!byId.TryGetValue(id, out var employee))
                {
                    refused.Add(new(id, "сотрудник не найден", "нет в справочнике — удалён или идентификатор неверен"));
                    continue;
                }
                var reason = AssignmentRules.Unfit(employee, kind, from, to)
                    ?? (assigned.GetValueOrDefault(id) is { } existing ? AssignmentRules.AlreadyAssigned(existing) : null);
                if (reason is not null) refused.Add(new(id, employee.FullName, reason));
                else created.Add((Assignment.Create(id, siteId, kind, from, to, now), employee));
            }

            if (refused.Count > 0)
                return Results.UnprocessableEntity(new
                {
                    error = "Назначение не заведено ни одному сотруднику: " +
                        string.Join("; ", refused.Select(r => $"{r.Name} — {r.Reason}")) +
                        ". Уберите их из пачки или измените период.",
                    refused,
                });

            db.Assignments.AddRange(created.Select(c => c.Assignment));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        foreach (var (assignment, employee) in created)
            await log.RecordAsync(AssignmentActions.Created, assignment.Id.ToString(),
                AssignmentActions.Label(employee.FullName, site.Name, kind), after: assignment.Period, ct: ct);

        var today = await clock.TodayOnSiteAsync(siteId, ct);
        var items = new List<AssignmentDto>();
        foreach (var (assignment, employee) in created)
            items.Add(await AssignmentViews.OneAsync(db, gate, assignment, employee, today, ct));
        return Results.Created($"/api/work/assignments?constructionId={siteId}", new { items });
    }

    /// <summary>Назначения стройки того же вида, пересекающиеся с периодом, — по сотруднику (первое по началу).</summary>
    private static async Task<Dictionary<Guid, Assignment>> OverlapsAsync(
        WorkDbContext db, Guid siteId, AssignmentKind kind, DateOnly from, DateOnly? to, CancellationToken ct) =>
        (await db.Assignments.AsNoTracking().Where(a => a.ConstructionId == siteId && a.Kind == kind).ToListAsync(ct))
            .Where(a => a.Overlaps(from, to))
            .GroupBy(a => a.EmployeeId)
            .ToDictionary(g => g.Key, g => g.OrderBy(a => a.DateFrom).First());
}
