using System.Data;
using System.Globalization;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Access;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Crew;

internal sealed class CrewGroups(WorkDbContext db, ReportReads reads, IModuleReaders readers, ManagerSites managers,
    IModuleEmployees employees, IModuleUnits units, IModuleConstructions constructions, IModuleWorkTypes workTypes,
    IModulePlanItems planItems, ReportJournal journal, ReportPeople people, TimeProvider time)
{
    private const string Path = "/api/work/crew-groups";
    private const string NotFound = "Группа сверки не найдена.";
    private const string Stale = "Группу тем временем изменили. Перечитайте группу и повторите утверждение.";

    public sealed record Approval(decimal? Total);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(Path).WithTags("Сверка бригад");
        var read = AppPolicies.AnyPermission(WorkModule.ReportReview, WorkModule.CrewApprove, WorkModule.ReportRead);
        group.MapGet("", (Guid? constructionId, DateOnly? from, DateOnly? to, CrewGroups crew, CancellationToken ct) =>
            crew.ListAsync(constructionId, from, to, ct)).RequireAuthorization(read);
        group.MapGet("/{id:guid}", (Guid id, CrewGroups crew, CancellationToken ct) =>
            crew.OneAsync(id, ct)).RequireAuthorization(read);
        group.MapPost("/{id:guid}/approve", (Guid id, Approval body, HttpRequest request, CrewGroups crew, CancellationToken ct) =>
            crew.ApproveAsync(id, body, request, ct)).RequireAuthorization(AppPolicies.Permission(WorkModule.CrewApprove));
    }

    private async Task<IResult> ListAsync(Guid? constructionId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        if (from > to) throw new InvalidRequestException("Начало периода (from) позже его конца (to).");
        var (viewer, approves) = await AccessAsync(ct);
        var scope = reads.CrewGroups(viewer.Reach);
        var query = scope;
        if (constructionId is { } site) query = query.Where(g => g.ConstructionId == site);
        if (from is { } start) query = query.Where(g => g.Date >= start);
        if (to is { } end) query = query.Where(g => g.Date <= end);
        var (items, groups, lines) = await ReadAsync(query, viewer, approves, ct);
        // Варианты отбора не исчезают, когда выбранная стройка или период исключили её группы из списка.
        var scopeSiteIds = await scope.Select(g => g.ConstructionId).Distinct().ToArrayAsync(ct);
        var siteIds = scopeSiteIds.Union(groups.Select(g => g.ConstructionId)).ToArray();
        var sites = siteIds.Length == 0 ? [] : await constructions.ListAsync(ct);
        var siteNames = sites.Where(s => siteIds.Contains(s.Id)).ToDictionary(s => s.Id, s => s.Name);
        var types = groups.Count == 0 ? [] : await workTypes.GetAsync([.. groups.Select(g => g.WorkTypeId).Distinct()], ct) ?? [];
        var variantIds = lines.Select(l => l.VariantId).ToHashSet();
        var unitIds = lines.Select(l => l.UnitId).Concat(groups.Where(g => g.UnitId.HasValue).Select(g => g.UnitId!.Value))
            .Distinct().ToArray();
        var measures = unitIds.Length == 0 ? [] : await units.GetAsync(unitIds, ct) ?? [];
        var sectionIds = lines.Select(l => l.SectionId).OfType<Guid>().ToHashSet();
        return Results.Ok(new
        {
            items,
            constructions = siteIds.Select(id => new { id, name = siteNames.GetValueOrDefault(id) ?? $"Стройка · {id}" })
                .OrderBy(s => s.name, StringComparer.CurrentCulture).ToArray(),
            workTypes = types.ToDictionary(t => t.Id, t => t.DisplayName),
            workVariants = types.SelectMany(t => t.Variants).Where(v => variantIds.Contains(v.Id)).ToDictionary(v => v.Id, v => v.Name),
            units = measures.ToDictionary(u => u.Id, u => u.Name),
            sections = sites.Where(s => siteIds.Contains(s.Id)).SelectMany(s => s.Sections)
                .Where(s => sectionIds.Contains(s.Id)).ToDictionary(s => s.Id, s => s.Name),
        });
    }

    private async Task<IResult> OneAsync(Guid id, CancellationToken ct)
    {
        var (viewer, approves) = await AccessAsync(ct);
        var (rows, _, _) = await ReadAsync(reads.CrewGroups(viewer.Reach).Where(g => g.Id == id), viewer, approves, ct);
        return Results.Ok(rows.SingleOrDefault() ?? throw new NotFoundException(NotFound));
    }

    private async Task<(ReportViewer Viewer, ModuleReach? Approves)> AccessAsync(CancellationToken ct)
    {
        var reader = await readers.ForRequestAsync(ct);
        var approves = reader.Allows(WorkModule.CrewApprove);
        var managed = !reader.Allows(WorkModule.ReportRead) || approves ? await managers.ReachAsync(reader, ct) : null;
        var viewer = ReportViewer.Of(reader.Allows(WorkModule.ReportRead) ? ModuleReach.All : managed!);
        return (viewer, approves ? managed : null);
    }

    private async Task<(List<object> Items, List<CrewGroupRecord> Groups, IReadOnlyList<CrewGroupLine> Lines)> ReadAsync(
        IQueryable<CrewGroupRecord> query, ReportViewer viewer, ModuleReach? approves, CancellationToken ct)
    {
        // Версия и строки ответа должны описывать одну редакцию группы.
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var groups = await query.AsNoTracking().OrderByDescending(g => g.Date).ThenBy(g => g.ConstructionId)
            .ThenBy(g => g.Shift).ThenBy(g => g.WorkTypeId).ToListAsync(ct);
        if (groups.Count == 0) return ([], [], []);
        var lines = await reads.CrewLinesAsync(viewer, query, ct);
        var byGroup = lines.ToLookup(l => l.GroupId);
        var submitted = (await reads.CrewSubmittedEmployeesAsync(viewer, query, lines, ct)).ToLookup(s => s.Shift, s => s.EmployeeId);
        var positions = await CrewPublication.ReadPositionsAsync(planItems, lines.Select(l => l.PlanItemId), ct);
        await tx.CommitAsync(ct);

        var names = await NamesAsync(lines, ct);
        var items = new List<object>(groups.Count);
        foreach (var group in groups)
        {
            var rows = byGroup[group.Id].ToArray();
            var publication = CrewPublication.Classify(group, rows, submitted[group.ShiftKey], positions);
            items.Add(View(group, rows, publication, approves?.Covers(group.ConstructionId, own: false) == true, names));
        }
        return (items, groups, lines);
    }

    private async Task<IResult> ApproveAsync(Guid id, Approval body, HttpRequest request, CancellationToken ct)
    {
        var reach = await managers.ReachAsync(await readers.ForRequestAsync(ct), ct);
        var viewer = ReportViewer.Of(reach);
        var query = reads.CrewGroups(reach).Where(g => g.Id == id);
        var key = await query.AsNoTracking().SingleOrDefaultAsync(ct) ?? throw new NotFoundException(NotFound);
        var seen = SeenVersions.Required(request, "группы сверки");
        var actor = await people.ActorAsync(viewer, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockCrewShiftAsync(key.ConstructionId, key.Date, key.Shift, ct);
        var group = await query.SingleAsync(ct);
        if (seen != VersionOf(group)) throw new ConflictException(Stale);
        var rows = await reads.CrewLinesAsync(viewer, query, ct);
        var unitIds = rows.Select(l => l.UnitId).Distinct().ToArray();
        if (unitIds.Length == 0) throw new ConflictException("В группе нет поданных строк для утверждения.");
        if (group.State == "approved") throw new ConflictException("Итог группы уже утверждён.");
        var unitNames = (await units.GetAsync(unitIds, ct) ?? []).ToDictionary(u => u.Id, u => u.Name);
        string UnitName(Guid unit) => unitNames.GetValueOrDefault(unit) ?? unit.ToString();
        if (unitIds.Length > 1)
            throw new InvalidRequestException("В группе разные единицы измерения: " +
                string.Join(", ", unitIds.Select(UnitName).Order(StringComparer.CurrentCulture)) + ".");
        var total = body.Total ?? throw new InvalidRequestException("Не назван итоговый объём (total).");
        ReportRules.EnsureVolume("Итог группы", "total", total);
        var unitId = unitIds.Single();
        var names = await NamesAsync(rows, ct);
        var positions = await CrewPublication.ReadPositionsAsync(planItems, rows.Select(l => l.PlanItemId), ct);
        var before = group.Total;
        var now = time.GetUtcNow();
        var change = Guid.NewGuid();
        group.Approve(total, unitId, actor, now);
        foreach (var line in rows.DistinctBy(l => l.ReportId).OrderBy(l => l.EmployeeId))
        {
            var seq = await db.NextReportSeqAsync(line.EmployeeId, ct);
            journal.AppendCrewApproval(line.ReportId, line.EmployeeId, group.Id, before, total, UnitName(unitId),
                actor, NameOf(line.EmployeeId, names), change, seq, now);
        }
        // Приёмка держит замок сотрудника. После захвата всех этих замков видны приёмки, завершившиеся до утверждения.
        var currentRows = await reads.CrewLinesAsync(viewer, query, ct);
        var submitted = await reads.CrewSubmittedEmployeesAsync(viewer, query, currentRows, ct);
        var publication = CrewPublication.Classify(group, currentRows, submitted.Select(s => s.EmployeeId), positions);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(View(group, currentRows, publication, true, names));
    }

    private async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<CrewGroupLine> lines, CancellationToken ct)
    {
        var ids = lines.Select(l => l.EmployeeId).Distinct().ToArray();
        return (await employees.GetAsync(ids, ct) ?? []).ToDictionary(e => e.Id, e => e.FullName);
    }

    private static string NameOf(Guid id, IReadOnlyDictionary<Guid, string> names) => names.GetValueOrDefault(id) ?? id.ToString();

    private static string VersionOf(CrewGroupRecord group) => group.Version.ToString(CultureInfo.InvariantCulture);

    private static object View(CrewGroupRecord g, IReadOnlyList<CrewGroupLine> rows, CrewPublication publication, bool canApprove,
        IReadOnlyDictionary<Guid, string> names) => new
    {
        g.Id, g.ConstructionId, g.Date, shift = Report.ShiftCode(g.Shift), g.WorkTypeId,
        version = VersionOf(g), state = g.State, mismatch = rows.Select(l => l.Volume).Distinct().Skip(1).Any(),
        g.Total, g.UnitId, g.ApprovedBy, g.ApprovedByName, g.ApprovedAt, publication.Unsubmitted,
        publication = new { publication.Published, publication.Reason, publication.Keys, publication.Accepted, publication.Participating },
        actions = canApprove && g.State != "approved" && rows.Count > 0 ? new[] { "approve" } : [],
        lines = rows.Select(l => new
        {
            l.Id, l.ReportId, l.EmployeeId, fullName = NameOf(l.EmployeeId, names), l.VariantId, l.Volume, l.UnitId, l.CrewRole,
        }).ToList(),
    };
}
