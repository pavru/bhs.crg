using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Tables;
using BHS.CRG.Modules.Work.Data;
using Microsoft.EntityFrameworkCore;
using static BHS.CRG.Modules.Work.Tables.WorkTables;
using static BHS.CRG.Modules.Work.Tables.WorkTableNames;

namespace BHS.CRG.Modules.Work.Tables;

internal sealed class ReportTableRows(
    ReportReads reads, WorkTableReaders readers, WorkTableNames names, LineTableRows lines) : IModuleTableRows
{
    public async Task<ModuleTablePage> ReadAsync(ModuleTableQuery query, CancellationToken ct)
    {
        var access = await readers.ResolveAsync(query.Reader, ct);
        var visible = reads.TableReports(access.Viewer);
        if (query.Row is { } key)
            visible = Guid.TryParseExact(key, "D", out var id) && id.ToString() == key
                ? visible.Where(r => r.Id == id)
                : visible.Where(_ => false);

        var own = access.Viewer.Reach.Kind == ModuleReachKind.Own;
        // Отбор, порядок, число и итоги работают над теми же клетками, которые прочтёт человек.
        var source = own
            ? visible.Select(r => new { Header = r, Content = (Report?)r })
            : visible.LeftJoin(reads.ReadableTableReports(access.Viewer, access.EmployeeId, access.UserId),
                header => header.Id, content => content.Id, (header, content) => new { Header = header, Content = content });
        var rows = source
            .Select(r => new Cells
            {
                Id = r.Header.Id,
                Date = (DateOnly?)r.Content!.Date,
                Shift = (ReportShift?)r.Content!.Shift,
                Employee = (Guid?)r.Content!.EmployeeId,
                Site = (Guid?)r.Content!.ConstructionId,
                Status = r.Header.Status,
                Hours = r.Content!.Hours,
                Submission = (r.Header.SubmittedAt ?? r.Header.WindowEndsAt).ToString(),
                // Во внешнем соединении ключ содержимого становится nullable в SQL.
                LineCount = ((Guid?)r.Content!.Id).HasValue ? r.Content.Lines.Count : null,
                EnteredBy = r.Content!.EnteredByName,
                Note = r.Content!.Note,
            });

        var people = await names.EmployeesAsync(rows.Select(r => r.Employee), ct);
        var sites = (await names.SitesAsync(ct)).ToDictionary(s => s.Id, s => s.Name);
        var sql = TableSql<Cells>.Describe(Reports, columns => columns
            .Date(Date, r => r.Date)
            .Choice(Shift, r => r.Shift, Shifts)
            .Lookup(Employee, r => r.Employee, people)
            .Lookup(Site, r => r.Site, sites)
            .Choice(Status, r => (ReportStatus?)r.Status, Statuses)
            .Number(Hours, r => r.Hours)
            .Text(SubmissionTime, r => r.Submission)
            .Number(LineCount, r => r.LineCount)
            .Text(EnteredBy, r => r.EnteredBy)
            .Fields(key => key == Note ? r => r.Note : _ => null));

        var filtered = sql.Where(rows, query.Filter);
        var count = await filtered.CountAsync(ct);
        var totals = await sql.TotalsAsync(filtered, query.Totals, ct);
        var sorted = (sql.OrderBy(filtered, query.Sort) ?? filtered.OrderByDescending(r => r.Date)).ThenBy(r => r.Id);
        var page = sorted.Skip(query.Offset);
        if (query.Limit is { } limit) page = page.Take(limit);
        var read = await page.ToListAsync(ct);

        var breakdown = query.Row is not null && read.SingleOrDefault() is { Date: not null } row
            ? await lines.BreakdownAsync(access, row.Id, ct)
            : null;
        return new([.. read.Select(r => Values(r, people, sites))], count, totals,
            Keys: [.. read.Select(r => r.Id.ToString())], Breakdown: breakdown, Outcome: access.Outcome);
    }

    private static IReadOnlyDictionary<string, object?> Values(
        Cells row, IReadOnlyDictionary<Guid, string> people, IReadOnlyDictionary<Guid, string> sites) =>
        new Dictionary<string, object?>
        {
            [Date] = row.Date,
            [Shift] = Label(Shifts, row.Shift),
            [Employee] = Label(people, row.Employee),
            [Site] = Label(sites, row.Site),
            [Status] = Statuses[row.Status],
            [Hours] = row.Hours,
            [SubmissionTime] = row.Submission,
            [LineCount] = row.LineCount,
            [EnteredBy] = row.EnteredBy,
            [Note] = row.Note,
        };

    private sealed class Cells
    {
        public Guid Id { get; init; }
        public DateOnly? Date { get; init; }
        public ReportShift? Shift { get; init; }
        public Guid? Employee { get; init; }
        public Guid? Site { get; init; }
        public ReportStatus Status { get; init; }
        public decimal? Hours { get; init; }
        public string? Submission { get; init; }
        public int? LineCount { get; init; }
        public string? EnteredBy { get; init; }
        public string? Note { get; init; }
    }
}
