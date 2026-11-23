using BHS.CRG.Api.Auth;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Work;

internal sealed class WorkTableWorld(AssignmentHost host)
{
    internal const string Reports = "/api/tables/work.reports";
    internal const string Lines = "/api/tables/work.lines";
    internal const string ReportsMarker = "system:table:work.reports";
    internal const string LinesMarker = "system:table:work.lines";
    internal static readonly DateTimeOffset Window = AssignmentHost.Now.AddHours(9);
    internal AssignmentWorld Assignments { get; } = new(host);

    internal async Task<(Guid Employee, HttpClient Client)> InstallerAsync(Guid employeeType, string name)
    {
        var employee = await Assignments.EmployeeAsync(employeeType, name);
        var (user, client) = await Assignments.UserAsync(name, WorkModule.ReportOwn, CorePermissions.DataSetsRead);
        await Assignments.LinkAsync(employee, user);
        return (employee, client);
    }

    internal async Task<(Guid Employee, HttpClient Client)> ManagerAsync(Guid employeeType, params Guid[] sites)
    {
        var employee = await Assignments.EmployeeAsync(employeeType, "Менеджер");
        var (user, client) = await Assignments.UserAsync("Менеджер", WorkModule.ReportReview, CorePermissions.DataSetsRead);
        await Assignments.LinkAsync(employee, user);
        foreach (var site in sites)
            await Assignments.AssignedAsync(employee, site, AssignmentKind.Manager, "2026-01-01", null);
        return (employee, client);
    }

    internal async Task<(Guid Type, Guid Unit, string Label)> ClassifierAsync()
    {
        var seed = new WorkTypeSeed(host);
        var (units, types) = await seed.ClassifierAsync();
        var unit = await seed.RecordAsync(units.Id, "м");
        var type = await seed.RecordAsync(types.Id, "Прокладка кабеля", WorkTypeSeed.WorkTypeData("1", "Прокладка кабеля", unit));
        return (type.Id, unit.Id, Assert.IsType<string>(type.DisplayName));
    }

    internal async Task<Report> ReportAsync(Guid employee, Guid site, Guid type, Guid unit,
        string location, decimal hours, bool submitted = true, params decimal[] volumes)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
        var report = Report.Create(Guid.NewGuid(), employee, site, new DateOnly(2026, 10, 31),
            ReportShift.Night, hours, "Примечание", 1, AssignmentHost.Now, Window);
        for (var i = 0; i < volumes.Length; i++)
        {
            var values = new ReportLineValues(Guid.NewGuid(), type, null, volumes[i], unit, null,
                location, null, false, null, [], []);
            report.Lines.Add(ReportLine.Create(report.Id, i, values, Guid.NewGuid()));
        }
        if (submitted) Submit(report);
        db.Add(report);
        await db.SaveChangesAsync();
        return report;
    }

    internal async Task SubmitAsync(Guid id)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
        var report = await db.FindAsync<Report>(id) ?? throw new InvalidOperationException("Отчёт для теста не найден.");
        Submit(report);
        await db.SaveChangesAsync();
    }

    private static void Submit(Report report) =>
        report.Submit(AssignmentHost.Now, ReportSubmitter.Manual, null, 2, AssignmentHost.Now);
}
