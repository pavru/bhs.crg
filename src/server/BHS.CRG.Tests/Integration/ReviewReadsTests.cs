using System.Data.Common;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>Пометки под охватом и количество обращений к БД после ревью work-16.</summary>
[Collection(AssignmentHost.Collection)]
public class ReviewReadsTests(AssignmentHost host) : IAsyncLifetime
{
    private readonly ReviewWorld world = new(host);

    public Task InitializeAsync() => world.SeedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Произвольные ID не открывают пометки чужой стройки или сотрудника; нет ID — нет запроса.</summary>
    [Fact]
    public async Task Пометки_читаются_только_в_охвате_одним_запросом()
    {
        var (petrovId, petrov) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (_, sidorov) = await world.Reports.InstallerAsync("Сидоров Семён Семёнович");
        var (mine, foreign) = (Guid.NewGuid(), Guid.NewGuid());
        await world.SubmittedAsync(petrov, mine, world.SiteA);
        await world.SubmittedAsync(sidorov, foreign, world.SiteB);
        var probe = new Commands();
        await using var db = Context(probe);
        var reads = new ReportReads(db);
        Guid[] ids = [mine, foreign, Guid.NewGuid()];

        var sites = await reads.MarksAsync(ReportViewer.Of(ModuleReach.OfSites([world.SiteA])), ids, null, default);
        Assert.Equal(mine, Assert.Single(sites).Key);
        Assert.Contains(ReviewMarks.SiteNotAssigned, sites[mine]);
        Assert.Single(probe.Sql);
        probe.Sql.Clear();

        var own = await reads.MarksAsync(ReportViewer.Own(petrovId), ids, null, default);
        Assert.Equal(mine, Assert.Single(own).Key);
        Assert.Single(probe.Sql);
        probe.Sql.Clear();

        Assert.Empty(await reads.MarksAsync(ReportViewer.Of(ModuleReach.OfSites([])), ids, null, default));
        Assert.Single(probe.Sql);
        probe.Sql.Clear();
        Assert.Empty(await reads.MarksAsync(ReportViewer.Own(petrovId), [], null, default));
        Assert.Empty(probe.Sql);
    }

    /// <summary>Все счётчики — одна агрегация, число и сотрудники — один запрос, пометки страницы — один запрос.</summary>
    [Fact]
    public async Task Очередь_считает_пометки_одной_агрегацией()
    {
        var (petrovId, petrov) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (sidorovId, sidorov) = await world.Reports.InstallerAsync("Сидоров Семён Семёнович");
        await world.SubmittedAsync(petrov, Guid.NewGuid(), world.SiteA);
        await world.SubmittedAsync(sidorov, Guid.NewGuid(), world.SiteA);
        var probe = new Commands();
        await using var db = Context(probe);
        await using var scope = host.Services.CreateAsyncScope();

        var page = await new ReportReads(db).QueueAsync(ReportViewer.Of(ModuleReach.All),
            new ReviewQueueFilter(null, null, null, null, null), null, 0, 1,
            scope.ServiceProvider.GetRequiredService<IModuleWorkTypes>(), default);

        Assert.Equal(2, page.Total);
        Assert.Equal(2, page.Counts[ReviewMarks.SiteNotAssigned]);
        Assert.Single(page.Items);
        Assert.Equal(new[] { petrovId, sidorovId }.Order(), page.PendingEmployeeIds.Order());
        Assert.Equal(5, probe.Sql.Count); // агрегация, строки автоподач, число и сотрудники, страница, флаги
        Assert.Single(probe.Sql, sql => sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase));
    }

    private WorkDbContext Context(Commands probe) => new(new DbContextOptionsBuilder<WorkDbContext>()
        .UseNpgsql(host.ConnectionString).AddInterceptors(probe).Options);

    private sealed class Commands : DbCommandInterceptor
    {
        public List<string> Sql { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Sql.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
