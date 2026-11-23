using System.Text.Json;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Submission;
using BHS.CRG.Modules.Work.Summary;

namespace BHS.CRG.Tests.Work;

public class SummaryLayoutTests
{
    private static readonly DateOnly October31 = new(2026, 10, 31);
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 17, 0, 0, TimeSpan.Zero);
    private readonly Guid employeeId = Guid.NewGuid();
    private readonly Guid siteId = Guid.NewGuid();

    [Fact]
    public void Прошедший_день_различает_отсутствие_назначения_отчёта_и_занятости()
    {
        var site = new SummarySite(siteId, "Владивосток", "Asia/Vladivostok", October31.AddDays(1));
        var employee = new ModuleEmployee(employeeId, "Монтажник", null, null, site.Today, false, ModuleEmployeeAccount.None);
        var row = SummaryLayout.Row(employeeId, employee, site, [October31.AddDays(-1), October31, site.Today],
            [Assigned(October31, site.Today)], [], Now);

        Assert.Equal(new[] { SummaryStates.NotAssigned, SummaryStates.NotReceived, SummaryStates.NotEmployed },
            row.Cells.Select(cell => Assert.Single(cell).State));
    }

    [Fact]
    public void Одна_ночная_не_создаёт_дневную_а_две_смены_сохраняют_свои_часы()
    {
        var site = new SummarySite(siteId, "Москва", "Europe/Moscow", October31);
        var night = Report(ReportShift.Night, 3.5m);
        var day = Report(ReportShift.Day, 0);

        var single = SummaryLayout.Row(employeeId, null, site, [October31], [], [night], Now);
        var nightEntry = Assert.Single(Assert.Single(single.Cells));
        Assert.Equal("night", nightEntry.Shift);
        Assert.Equal(3.5m, nightEntry.Hours);

        var two = SummaryLayout.Row(employeeId, null, site, [October31], [], [night, day], Now);
        Assert.Collection(Assert.Single(two.Cells),
            entry =>
            {
                Assert.Equal("day", entry.Shift);
                Assert.Equal(SummaryStates.ZeroHours, entry.State);
                Assert.Equal(0m, entry.Hours);
            },
            entry => Assert.Equal(nightEntry, entry));
    }

    [Fact]
    public void В_конце_окна_по_правилу_подачи_вчерашний_день_становится_не_поступал()
    {
        var site = new SummarySite(siteId, "Нью-Йорк", "America/New_York", new DateOnly(2026, 11, 2));
        var date = site.Today.AddDays(-1); // Сутки перехода на зимнее время.
        var deadline = SubmissionWindow.EndsAt(site.TimeZoneId, date, ReportShift.Day);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 5, 0, 0, TimeSpan.Zero), deadline);
        var before = SummaryLayout.Row(employeeId, null, site with { Today = date }, [date],
            [Assigned(date, date)], [], deadline.AddTicks(-1));
        var after = SummaryLayout.Row(employeeId, null, site, [date], [Assigned(date, date)], [], deadline);

        Assert.Equal(SummaryStates.WindowOpen, Assert.Single(Assert.Single(before.Cells)).State);
        Assert.Equal(SummaryStates.NotReceived, Assert.Single(Assert.Single(after.Cells)).State);
    }

    [Fact]
    public void Черновик_сериализуется_только_со_сменой_кодом_и_сохранённым_моментом()
    {
        var site = new SummarySite(siteId, "Москва", "Europe/Moscow", October31);
        var when = Now.AddHours(8);
        var draft = Report(ReportShift.Night, 7) with { Status = ReportStatus.Draft, AutoSubmitAt = when };
        var row = SummaryLayout.Row(employeeId, null, site, [October31], [], [draft], Now);
        var entry = JsonSerializer.SerializeToElement(Assert.Single(Assert.Single(row.Cells)), JsonSerializerOptions.Web);

        Assert.Equal(new[] { "autoSubmitAt", "shift", "state" }, entry.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(SummaryStates.Draft, entry.GetProperty("state").GetString());
        Assert.Equal("night", entry.GetProperty("shift").GetString());
        Assert.Equal(when, entry.GetProperty("autoSubmitAt").GetDateTimeOffset());
    }

    private Assignment Assigned(DateOnly from, DateOnly to) =>
        Assignment.Create(employeeId, siteId, AssignmentKind.Installer, from, to, Now);

    private SummaryReport Report(ReportShift shift, decimal hours) =>
        new(employeeId, siteId, October31, shift, ReportStatus.Submitted, Guid.NewGuid(), hours, null);
}
