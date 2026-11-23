using BHS.CRG.Modules.Tables;
using BHS.CRG.Modules.Work.Data;
using Microsoft.EntityFrameworkCore;
using static BHS.CRG.Modules.Work.Tables.WorkTables;
using static BHS.CRG.Modules.Work.Tables.WorkTableNames;

namespace BHS.CRG.Modules.Work.Tables;

internal sealed class LineTableRows(ReportReads reads, WorkTableReaders readers, WorkTableNames names) : IModuleTableRows
{
    public async Task<ModuleTablePage> ReadAsync(ModuleTableQuery query, CancellationToken ct)
    {
        var access = await readers.ResolveAsync(query.Reader, ct);
        var rows = Rows(access);
        if (query.Row is { } key)
            rows = Guid.TryParseExact(key, "D", out var id) && id.ToString() == key
                ? rows.Where(r => r.Id == id)
                : rows.Where(_ => false);

        var people = await names.EmployeesAsync(rows.Select(r => (Guid?)r.EmployeeId), ct);
        var constructions = await names.SitesAsync(ct);
        var sites = constructions.ToDictionary(s => s.Id, s => s.Name);
        var labels = await LineLabelsAsync(rows, constructions.SelectMany(s => s.Sections).ToDictionary(s => s.Id, s => s.Name), ct);
        var sql = TableSql<Cells>.Describe(Lines, columns => columns
            .Date(Date, r => r.Date)
            .Choice(Shift, r => (ReportShift?)r.Shift, Shifts)
            .Lookup(Employee, r => (Guid?)r.EmployeeId, people)
            .Lookup(Site, r => (Guid?)r.SiteId, sites)
            .Choice(Status, r => (ReportStatus?)r.Status, Statuses)
            .Lookup(Section, r => r.SectionId, labels.Sections)
            .Lookup(WorkType, r => (Guid?)r.WorkTypeId, labels.Types)
            .Lookup(Variant, r => r.VariantId, labels.Variants)
            .Number(Volume, r => r.Volume)
            .Lookup(Unit, r => (Guid?)r.UnitId, labels.Units)
            .Text(Location, r => r.Location)
            .Flag(IsCrew, r => r.IsCrew));

        var filtered = sql.Where(rows, query.Filter);
        var count = await filtered.CountAsync(ct);
        var totals = await sql.TotalsAsync(filtered, query.Totals, ct);
        var sorted = (sql.OrderBy(filtered, query.Sort) ?? filtered.OrderByDescending(r => r.Date))
            .ThenBy(r => r.ReportId).ThenBy(r => r.Position).ThenBy(r => r.Id);
        var page = sorted.Skip(query.Offset);
        if (query.Limit is { } limit) page = page.Take(limit);
        var read = await page.ToListAsync(ct);

        return new([.. read.Select(r => Values(r, people, sites, labels))], count, totals,
            Keys: [.. read.Select(r => r.Id.ToString())], Outcome: access.Outcome);
    }

    internal async Task<TableRowBreakdown> BreakdownAsync(WorkTableAccess access, Guid reportId, CancellationToken ct)
    {
        var rows = Rows(access).Where(r => r.ReportId == reportId);
        var constructions = await names.SitesAsync(ct);
        var sections = constructions.SelectMany(s => s.Sections).ToDictionary(s => s.Id, s => s.Name);
        var labels = await LineLabelsAsync(rows, sections, ct);
        var lines = await rows.OrderBy(r => r.Position).ThenBy(r => r.Id).ToListAsync(ct);
        return new([.. lines.Select(line => new TableBreakdownRow(LineValues(line, labels, breakdown: true)))], Narrowed: false);
    }

    private IQueryable<Cells> Rows(WorkTableAccess access) =>
        reads.ReadableTableReports(access.Viewer, access.EmployeeId, access.UserId)
            .SelectMany(r => r.Lines, (r, line) => new Cells
            {
                Id = line.Id, Position = line.Position, ReportId = r.Id, Date = r.Date, Shift = r.Shift,
                EmployeeId = r.EmployeeId, SiteId = r.ConstructionId, Status = r.Status, SectionId = line.SectionId,
                WorkTypeId = line.WorkTypeId, VariantId = line.VariantId, Volume = line.Volume, UnitId = line.UnitId,
                Location = line.Location, IsCrew = line.IsCrew,
            });

    private async Task<LineLabels> LineLabelsAsync(
        IQueryable<Cells> rows, IReadOnlyDictionary<Guid, string> sections, CancellationToken ct) => new(
        sections,
        await names.WorkTypesAsync(rows.Select(r => (Guid?)r.WorkTypeId), ct),
        await names.VariantsAsync(rows.Select(r => r.VariantId), ct),
        await names.UnitsAsync(rows.Select(r => (Guid?)r.UnitId), ct));

    private static IReadOnlyDictionary<string, object?> Values(Cells row, IReadOnlyDictionary<Guid, string> people,
        IReadOnlyDictionary<Guid, string> sites, LineLabels labels)
    {
        var cells = LineValues(row, labels);
        cells[Date] = row.Date;
        cells[Shift] = Shifts[row.Shift];
        cells[Employee] = people.GetValueOrDefault(row.EmployeeId);
        cells[Site] = sites.GetValueOrDefault(row.SiteId);
        cells[Status] = Statuses[row.Status];
        return cells;
    }

    private static Dictionary<string, object?> LineValues(Cells line, LineLabels labels, bool breakdown = false) => new()
    {
        [Section] = Label(labels.Sections, line.SectionId),
        [WorkType] = labels.Types.GetValueOrDefault(line.WorkTypeId),
        [Variant] = Label(labels.Variants, line.VariantId),
        [Volume] = line.Volume,
        [Unit] = labels.Units.GetValueOrDefault(line.UnitId),
        [Location] = line.Location,
        [IsCrew] = breakdown ? (line.IsCrew ? "Да" : "Нет") : line.IsCrew,
    };

    private sealed class Cells
    {
        public Guid Id { get; init; }
        public int Position { get; init; }
        public Guid ReportId { get; init; }
        public DateOnly Date { get; init; }
        public ReportShift Shift { get; init; }
        public Guid EmployeeId { get; init; }
        public Guid SiteId { get; init; }
        public ReportStatus Status { get; init; }
        public Guid? SectionId { get; init; }
        public Guid WorkTypeId { get; init; }
        public Guid? VariantId { get; init; }
        public decimal Volume { get; init; }
        public Guid UnitId { get; init; }
        public string? Location { get; init; }
        public bool IsCrew { get; init; }
    }

    private sealed record LineLabels(IReadOnlyDictionary<Guid, string> Sections, IReadOnlyDictionary<Guid, string> Types,
        IReadOnlyDictionary<Guid, string> Variants, IReadOnlyDictionary<Guid, string> Units);
}
