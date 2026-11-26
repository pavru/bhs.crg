using BHS.CRG.Api.Modules;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Work;

public sealed class WorkLaborHost : AssignmentHost
{
    protected override string ModuleCode => "plan,work";
    protected override string DatabaseSuffix => "worklabor";
}

[CollectionDefinition(Name)]
public sealed class WorkLaborCollection : ICollectionFixture<WorkLaborHost>
{
    public const string Name = "Work labor";
}

internal sealed class WorkLaborWorld(AssignmentHost host)
{
    public static readonly DateOnly Day = new(2026, 11, 16);
    public ReportWorld Reports { get; } = new(host);

    public Task SeedAsync() => Reports.SeedAsync();

    public static string Estimate(Guid site, Guid? section = null) =>
        $"/api/work/labor/estimate-vs-actual?constructionId={site}&from=2026-11-01&to=2026-11-30"
        + (section is { } id ? $"&sectionId={id}" : "");

    public string Productivity(DateOnly? from = null, DateOnly? to = null) =>
        $"/api/work/labor/productivity?workTypeId={Reports.WorkType}&unitId={Reports.Unit}"
        + $"&from={from ?? Day:yyyy-MM-dd}&to={to ?? Day:yyyy-MM-dd}";

    public async Task<Guid> SectionAsync(Guid site, string name)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var section = Section.Create(site, name);
        db.Sections.Add(section);
        await db.SaveChangesAsync();
        return section.Id;
    }

    public ReportLineValues Line(decimal volume, Guid? section = null, Guid? type = null, Guid? unit = null) =>
        new(Guid.NewGuid(), type ?? Reports.WorkType, null, volume, unit ?? Reports.Unit, section, null, null,
            false, null, [], []);

    public async Task ReportAsync(Guid site, Guid employee, decimal? hours, ReportStatus status,
        DateOnly date, params ReportLineValues[] lines)
    {
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<WorkDbContext>();
        var positions = services.GetRequiredService<IModulePlanItems>();
        var now = ReviewWorld.Evening16.ToUniversalTime();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var report = Report.Create(Guid.NewGuid(), employee, site, date, ReportShift.Day, hours, null,
            await db.NextReportSeqAsync(employee, default), now, now.AddDays(1));
        foreach (var line in lines)
        {
            var position = (await positions.FindOrCreateAsync(line.KeyOn(site))).Item;
            report.Lines.Add(ReportLine.Create(report.Id, report.Lines.Count, line, position!.Id));
        }
        if (status != ReportStatus.Draft) report.Submit(now, ReportSubmitter.Manual, null, report.Seq, now);
        if (status == ReportStatus.Accepted) report.Accept(report.Seq, now);
        db.Add(report);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public async Task NormAsync(Guid site, Guid? section, decimal hoursPerUnit)
    {
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;
        await services.ProjectModuleTypesAsync();
        var db = services.GetRequiredService<PlanDbContext>();
        var estimate = BHS.CRG.Modules.Plan.Data.Estimate.Create(site, section, "Смета", EstimateKind.Main, DateTimeOffset.UtcNow);
        db.Estimates.Add(estimate);
        await db.SaveChangesAsync();
        var position = (await services.GetRequiredService<IModulePlanItems>().FindOrCreateAsync(
            new(Reports.WorkType, site, section, Reports.Unit))).Item!.Id;
        await services.GetRequiredService<EstimateRevisionWriter>().AddAsync(estimate.Id, new("смета.gsfx"),
        [
            new("Прокладка кабеля", UnitId: Reports.Unit, WorkTypeId: Reports.WorkType, PlanItemId: position,
                Factor: 1, Volume: 10, SourceVolume: 10, UnitCost: 100, Total: 1000, Labor: [hoursPerUnit * 10]),
        ]);
    }
}
