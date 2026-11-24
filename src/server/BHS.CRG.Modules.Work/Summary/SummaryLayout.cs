using System.Text.Json.Serialization;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Submission;

namespace BHS.CRG.Modules.Work.Summary;

public sealed record SummarySite(Guid Id, string Name, string TimeZoneId, DateOnly Today);

public sealed record SummaryReport(
    Guid EmployeeId, Guid ConstructionId, DateOnly Date, ReportShift Shift, ReportStatus Status,
    Guid? ReportId, decimal? Hours, DateTimeOffset? AutoSubmitAt);

public sealed record SummaryEntry(
    string Shift, string State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ReportId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? Hours = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Status = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? AutoSubmitAt = null);

public sealed record SummaryContact(string State = "not-tracked");

public sealed record SummaryRow(
    Guid EmployeeId, string EmployeeName, Guid ConstructionId, string ConstructionName, string TimeZoneId,
    IReadOnlyList<IReadOnlyList<SummaryEntry>> Cells)
{
    public SummaryContact Contact { get; } = new();
}

public static class SummaryLayout
{
    internal const string UnknownEmployee = "Сотрудник недоступен";

    public static SummaryRow Row(
        Guid employeeId, ModuleEmployee? employee, SummarySite site, IReadOnlyList<DateOnly> days,
        IEnumerable<Assignment> assignments, IEnumerable<SummaryReport> reports, DateTimeOffset now)
    {
        var byDay = reports.ToLookup(r => r.Date);
        var periods = assignments.ToArray();
        var cells = days.Select(day => Cell(day, site, employee, periods, byDay[day], now)).ToArray();
        return new(employeeId, employee?.FullName ?? UnknownEmployee, site.Id, site.Name, site.TimeZoneId, cells);
    }

    private static IReadOnlyList<SummaryEntry> Cell(
        DateOnly date, SummarySite site, ModuleEmployee? employee, Assignment[] assignments,
        IEnumerable<SummaryReport> reports, DateTimeOffset now)
    {
        var entries = reports.OrderBy(r => r.Shift).Select(Entry).ToArray();
        if (entries.Length > 0) return entries;

        var state = date > site.Today ? SummaryStates.Future
            : employee?.NotWorkingOn(date) is not null ? SummaryStates.NotEmployed
            : !assignments.Any(a => a.Covers(date)) ? SummaryStates.NotAssigned
            : now < SubmissionWindow.EndsAt(site.TimeZoneId, date, ReportShift.Day) ? SummaryStates.WindowOpen
            : SummaryStates.NotReceived;
        return [new(Report.ShiftCode(ReportShift.Day), state)];
    }

    private static SummaryEntry Entry(SummaryReport report)
    {
        var shift = Report.ShiftCode(report.Shift);
        return report.Status == ReportStatus.Draft
            ? new(shift, SummaryStates.Draft, AutoSubmitAt: report.AutoSubmitAt)
            : new(shift, report.Hours == 0 ? SummaryStates.ZeroHours : SummaryStates.Report,
                report.ReportId, report.Hours, Report.StatusCode(report.Status));
    }
}
