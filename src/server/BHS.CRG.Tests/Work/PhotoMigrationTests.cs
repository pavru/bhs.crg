using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BHS.CRG.Tests.Work;

/// <summary>Up/Down миграции фото на отдельной базе: прежние отчёты и строки сохраняются.</summary>
public class PhotoMigrationTests
{
    [Fact]
    public async Task Миграция_закрытия_сохраняет_прежние_фото_без_вымышленной_метки()
    {
        var connection = TestDatabases.ConnectionString("photo_migrate");
        await using var run = await TestRunDatabase.LockAsync(connection, TimeSpan.FromSeconds(10));
        var options = new DbContextOptionsBuilder<WorkDbContext>();
        WorkDbContext.Configure(options, connection);
        await using var db = new WorkDbContext(options.Options);
        var migrator = db.GetService<IMigrator>();
        var migrations = db.Database.GetMigrations().ToList();
        var at = migrations.FindIndex(m => m.EndsWith("_RecordPhotoClosing", StringComparison.Ordinal));
        Assert.True(at > 0);
        try
        {
            await migrator.MigrateAsync(Migration.InitialDatabase);
            await migrator.MigrateAsync(migrations[at - 1]);
            var (_, line) = await WorkMigrationRows.AddAsync(db, Guid.NewGuid(), Guid.NewGuid(), "draft", isCrew: false);
            var id = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO work.line_photos (id, line_id, state, declared_at) VALUES ({id}, {line}, 'pending', {now})
                """);
            await db.Database.MigrateAsync();

            var photo = await db.Set<LinePhoto>().AsNoTracking().SingleAsync(p => p.Id == id);
            Assert.Equal(LinePhotoState.Pending, photo.State);
            Assert.Null(photo.DeclaredInClosingId);
            Assert.Null(photo.StoredInClosingId);
        }
        finally
        {
            await migrator.MigrateAsync(Migration.InitialDatabase);
        }
    }

    [Fact]
    public async Task Фото_поднимаются_откатываются_и_поднимаются_повторно()
    {
        var connection = TestDatabases.ConnectionString("photo_migrate");
        await using var run = await TestRunDatabase.LockAsync(connection, TimeSpan.FromSeconds(10));
        var options = new DbContextOptionsBuilder<WorkDbContext>();
        WorkDbContext.Configure(options, connection);
        await using var db = new WorkDbContext(options.Options);
        var migrator = db.GetService<IMigrator>();
        var migrations = db.Database.GetMigrations().ToList();
        var at = migrations.FindIndex(m => m.EndsWith("_LinePhotos", StringComparison.Ordinal));
        Assert.True(at > 0);
        var previous = migrations[at - 1];
        try
        {
            await migrator.MigrateAsync(Migration.InitialDatabase);
            await migrator.MigrateAsync(previous);
            var now = DateTimeOffset.UtcNow;
            var (_, lineId) = await WorkMigrationRows.AddAsync(db, Guid.NewGuid(), Guid.NewGuid(), "draft", isCrew: false);

            await db.Database.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
            Assert.Equal(1, await db.Set<Report>().Select(r => r.CurrentVersion).SingleAsync());
            var pending = LinePhoto.Declared(Guid.NewGuid(), lineId, now);
            db.Add(pending);
            db.Add(LinePhotoRemoval.Of(LinePhoto.Declared(Guid.NewGuid(), lineId, now), now));
            await db.SaveChangesAsync();
            Assert.Single(await db.Set<LinePhoto>().AsNoTracking().ToListAsync());
            Assert.Single(await db.Set<LinePhotoRemoval>().AsNoTracking().ToListAsync());

            db.ChangeTracker.Clear();
            await migrator.MigrateAsync(previous);
            Assert.Equal("прежний отчёт", await db.Set<Report>().Select(r => r.Note).SingleAsync());
            Assert.Equal(lineId, await db.Database.SqlQueryRaw<Guid>("SELECT id AS \"Value\" FROM work.report_lines").SingleAsync());

            await db.Database.MigrateAsync();
            Assert.Empty(await db.Set<LinePhoto>().ToListAsync());
            Assert.Empty(await db.Set<LinePhotoRemoval>().ToListAsync());
            Assert.Equal(1, await db.Set<Report>().CountAsync());
        }
        finally
        {
            // У этой базы только схема work; после проверки оставляем её без таблиц и истории модуля.
            await migrator.MigrateAsync(Migration.InitialDatabase);
        }
    }
}
