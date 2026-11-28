using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Schedules;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Tables;
using Microsoft.EntityFrameworkCore;
using static BHS.CRG.Modules.Tables.ModuleTableColumnKind;

namespace BHS.CRG.Modules.Plan.Tables;

internal sealed class ScheduleWorksTable(
    PlanDbContext db, IModuleConstructions constructions, IModulePlanItems items,
    IModuleWorkTypes workTypes, IModuleEmployees employees, ScheduleBuilder schedules) : IModuleTableRows
{
    private const string ScheduleName = "График", Site = "Стройка", Section = "Раздел", Work = "Работа";
    private const string WorkType = "ВидРаботы", Start = "Начало", End = "Окончание", Volume = "Объём";
    private const string Assignees = "Исполнители", Warnings = "Предупреждения";
    private const string ScheduleKey = "КлючГрафика", SectionKey = "КлючРаздела";

    internal static readonly ModuleTable Declaration = new(
        "schedule-works", "Работы графиков", "работа графика", PlanModule.ScheduleRead, ModuleTableIsolation.None,
        ModuleTableBoundary.Only("Работы графиков всех строек"),
        [
            new(ScheduleName, "График", Text),
            new(Site, "Стройка", Text, Sites: true),
            new(Section, "Раздел", Text),
            new(Work, "Работа", Text),
            new(WorkType, "Вид работы", Text),
            new(Start, "Начало", ModuleTableColumnKind.Date),
            new(End, "Окончание", ModuleTableColumnKind.Date),
            new(Volume, "Объём", Number),
            new(Assignees, "Исполнители", List),
            new(Warnings, "Предупреждения", Text),
            // Имена графиков и разделов могут совпадать. Переход из графика ставит отбор по ключам.
            new(ScheduleKey, "Ключ графика", Text, OnDemand: true),
            new(SectionKey, "Ключ раздела", Text, OnDemand: true),
        ],
        typeof(ScheduleWorksTable),
        Views: [new("works", "Работы графиков",
            [ScheduleName, Site, Section, Work, WorkType, Start, End, Volume, Assignees, Warnings],
            Filters: [ScheduleName, Site, Section, WorkType, Start, End, Assignees, Warnings])],
        MissingRowIsNotFound: true);

    public async Task<ModuleTablePage> ReadAsync(ModuleTableQuery query, CancellationToken ct)
    {
        var rows = from work in db.ScheduleWorks.AsNoTracking()
                   join schedule in db.Schedules.AsNoTracking() on work.ScheduleId equals schedule.Id
                   select new Cells { Work = work, Schedule = schedule };
        if (query.Row is { } key)
            rows = Guid.TryParseExact(key, "D", out var id) && id.ToString() == key
                ? rows.Where(r => r.Work.Id == id)
                : rows.Where(_ => false);

        var places = await constructions.ListAsync(ct);
        var sites = places.ToDictionary(s => s.Id, s => s.Name);
        var sections = places.SelectMany(s => s.Sections).ToDictionary(s => s.Id, s => s.Name);
        var itemIds = await rows.Select(r => r.Work.PlanItemId).Distinct().ToArrayAsync(ct);
        var positions = await items.GetAsync(itemIds, ct);
        var typeIds = positions.Select(p => p.Key.WorkTypeId).Distinct().ToArray();
        var types = typeIds.Length == 0 ? [] : await workTypes.GetAsync(typeIds, ct) ?? [];
        var typeNames = types.ToDictionary(t => t.Id, t => t.DisplayName);
        var itemNames = positions.Where(p => typeNames.ContainsKey(p.Key.WorkTypeId))
            .ToDictionary(p => p.Id, p => typeNames[p.Key.WorkTypeId]);
        var assignments = db.ScheduleWorkAssignees.AsNoTracking().Where(a => rows.Any(r => r.Work.Id == a.WorkId));
        var employeeIds = await assignments.Select(a => a.EmployeeId).Distinct().ToArrayAsync(ct);
        var people = employeeIds.Length == 0 ? [] : await employees.GetAsync(employeeIds, ct) ?? [];
        var names = people.ToDictionary(e => e.Id, e => e.FullName);
        var warningIds = TableFilters.Mentions(query.Filter, Warnings) || query.Sort?.Any(s => s.Column == Warnings) == true
            || query.Totals?.ContainsKey(Warnings) == true
            ? await rows.Select(r => r.Schedule.Id).Distinct().ToArrayAsync(ct) : [];
        var warnings = await WarningLabelsAsync(warningIds, ct);
        var sql = TableSql<Cells>.Describe(Declaration, columns => columns
            .Text(ScheduleName, r => r.Schedule.Name)
            .Lookup(Site, r => (Guid?)r.Schedule.ConstructionId, sites)
            .Lookup(Section, r => r.Work.SectionId, sections)
            .Text(Work, r => r.Work.Name)
            .Lookup(WorkType, r => (Guid?)r.Work.PlanItemId, itemNames)
            .Date(Start, r => r.Work.Start)
            .Date(End, r => r.Work.End)
            .Number(Volume, r => r.Work.Volume)
            .List(Assignees, r => db.ScheduleWorkAssignees.Where(a => a.WorkId == r.Work.Id)
                .Select(a => (Guid?)a.EmployeeId), names, "Сотрудник недоступен")
            .Lookup(Warnings, r => (Guid?)r.Work.Id, warnings)
            .Text(ScheduleKey, r => r.Schedule.Id.ToString())
            .Text(SectionKey, r => r.Work.SectionId == null ? null : r.Work.SectionId.Value.ToString()));

        var filtered = sql.Where(rows, query.Filter);
        var count = await filtered.CountAsync(ct);
        var totals = await sql.TotalsAsync(filtered, query.Totals, ct);
        var sorted = (sql.OrderBy(filtered, query.Sort) ?? filtered.OrderBy(r => r.Schedule.Name))
            .ThenBy(r => r.Schedule.Id).ThenBy(r => r.Work.Name).ThenBy(r => r.Work.Id);
        var page = sorted.Skip(query.Offset);
        if (query.Limit is { } limit) page = page.Take(limit);
        var read = await page.ToListAsync(ct);
        var pageWorkIds = read.Select(r => r.Work.Id).ToArray();
        var assigned = (await assignments.Where(a => pageWorkIds.Contains(a.WorkId)).ToListAsync(ct))
            .ToLookup(a => a.WorkId, a => names.GetValueOrDefault(a.EmployeeId, "Сотрудник недоступен"));
        if (warningIds.Length == 0 && query.Columns.Contains(Warnings))
            warnings = await WarningLabelsAsync(read.Select(r => r.Schedule.Id).Distinct(), ct);

        return new([.. read.Select(r => new Dictionary<string, object?>
        {
            [ScheduleName] = r.Schedule.Name,
            [Site] = sites.GetValueOrDefault(r.Schedule.ConstructionId),
            [Section] = r.Work.SectionId is { } section ? sections.GetValueOrDefault(section) : null,
            [Work] = r.Work.Name,
            [WorkType] = itemNames.GetValueOrDefault(r.Work.PlanItemId),
            [Start] = r.Work.Start,
            [End] = r.Work.End,
            [Volume] = r.Work.Volume,
            [Assignees] = assigned[r.Work.Id].Distinct().Order(StringComparer.CurrentCulture).ToArray(),
            [Warnings] = warnings.GetValueOrDefault(r.Work.Id),
            [ScheduleKey] = r.Schedule.Id.ToString(),
            [SectionKey] = r.Work.SectionId?.ToString(),
        })], count, totals, Keys: [.. pageWorkIds.Select(id => id.ToString())]);
    }

    private async Task<Dictionary<Guid, string>> WarningLabelsAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var labels = new Dictionary<Guid, string>();
        foreach (var id in ids)
        {
            var graph = await schedules.ReadAsync(id, ct);
            foreach (var work in graph.Works)
            {
                var codes = (work.Warnings ?? []).Select(w => w.Code).Concat(graph.Marks.Select(m => m.Code))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
                var label = string.Join(", ", codes);
                if (label.Length > 0) labels.Add(work.Id, label);
            }
        }
        return labels;
    }

    private sealed class Cells
    {
        public ScheduleWork Work { get; init; } = null!;
        public Schedule Schedule { get; init; } = null!;
    }
}
