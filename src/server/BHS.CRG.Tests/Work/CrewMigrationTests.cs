using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BHS.CRG.Tests.Work;

public class CrewMigrationTests
{
    [Fact]
    public async Task Migration_backfills_legacy_roles_and_one_group_per_submitted_work_type_and_shift()
    {
        var connection = TestDatabases.ConnectionString("crew_migrate");
        await using var run = await TestRunDatabase.LockAsync(connection, TimeSpan.FromSeconds(10));
        var options = new DbContextOptionsBuilder<WorkDbContext>();
        WorkDbContext.Configure(options, connection);
        await using var db = new WorkDbContext(options.Options);
        var migrator = db.GetService<IMigrator>();
        var previous = db.Database.GetMigrations().Single(m => m.EndsWith("_LinePhotos", StringComparison.Ordinal));
        try
        {
            await migrator.MigrateAsync(Migration.InitialDatabase);
            await migrator.MigrateAsync(previous);
            var site = Guid.NewGuid();
            var workType = Guid.NewGuid();
            await WorkMigrationRows.AddAsync(db, site, workType, "submitted", isCrew: true);
            await WorkMigrationRows.AddAsync(db, site, workType, "accepted", isCrew: true);
            await WorkMigrationRows.AddAsync(db, site, workType, "submitted", isCrew: true, shift: "night");
            await WorkMigrationRows.AddAsync(db, site, Guid.NewGuid(), "draft", isCrew: true);
            await WorkMigrationRows.AddAsync(db, site, Guid.NewGuid(), "submitted", isCrew: false);

            await db.Database.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
            var roles = await db.Set<ReportLine>().AsNoTracking().ToListAsync();
            Assert.Equal(4, roles.Count(l => l.CrewRole == "lead"));
            Assert.Null(Assert.Single(roles, l => !l.IsCrew).CrewRole);
            var shifts = await db.Database.SqlQueryRaw<string>("SELECT shift FROM work.crew_groups ORDER BY shift").ToListAsync();
            Assert.Equal(new[] { "day", "night" }, shifts);

            // Старая резервная копия вставляет строки уже в новую схему без колонки crew_role.
            var (_, restored) = await WorkMigrationRows.AddAsync(db, Guid.NewGuid(), workType, "submitted", isCrew: true);
            var restoredLine = await db.Set<ReportLine>().AsNoTracking().SingleAsync(l => l.Id == restored);
            Assert.Equal("lead", restoredLine.CrewRole);
            Assert.Equal(3, await db.Database.SqlQueryRaw<int>("SELECT 1 FROM work.crew_groups").CountAsync());

            await migrator.MigrateAsync(previous);
            Assert.Equal(6, await db.Set<Report>().CountAsync());
        }
        finally
        {
            await migrator.MigrateAsync(Migration.InitialDatabase);
        }
    }
}
