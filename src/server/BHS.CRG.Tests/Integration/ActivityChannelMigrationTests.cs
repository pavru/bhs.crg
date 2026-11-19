using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Миграции <c>ActivityChannel</c> и <c>JobChannel</c> (ТЗ CORE-28.1, issue #1336, design D5): записи
/// журнала и задачи, сделанные до появления канала, получают его по автору — без автора <c>system</c>,
/// с автором <c>ui</c>.
///
/// <para>Проверяется на своей базе, доведённой до миграции ПЕРЕД <c>ActivityChannel</c>: только там есть
/// строки без колонки. На базе прогона миграция прошла по пустой таблице и не проверила бы ничего. Без
/// заполнения в миграции все старые записи получили бы одно умолчание колонки — и действия системы
/// выглядели бы в журнале действиями интерфейса.</para>
/// </summary>
public class ActivityChannelMigrationTests : IAsyncLifetime
{
    private readonly string _name = TestDatabases.Name("activity_channel");

    public async Task InitializeAsync()
    {
        await DropAsync();
        await AdminAsync($"CREATE DATABASE {TestDatabases.Quote(_name)}");
    }

    public async Task DisposeAsync()
    {
        if (Environment.GetEnvironmentVariable("BHS_TEST_KEEP_DB") is not { Length: > 0 }) await DropAsync();
    }

    [Fact]
    public async Task Старые_записи_и_задачи_получают_канал_по_автору()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(IntegrationTestFixture.TestConnectionString)
            {
                Database = _name,
                CommandTimeout = 300,
            }.ConnectionString)
            .Options);

        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_ActivityChannel", StringComparison.Ordinal));
        Assert.True(index > 0, "Миграции ActivityChannel в сборке нет — тест проверяет не то, что назвал.");
        Assert.Contains(all.Skip(index), m => m.EndsWith("_JobChannel", StringComparison.Ordinal));
        await db.GetService<IMigrator>().MigrateAsync(all[index - 1]);

        Guid bySystem = Guid.NewGuid(), byPerson = Guid.NewGuid(), person = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO activity_log ("Id", "OccurredAt", "Action", "ActorId", "ActorName", "SessionId", "SeriesHead")
            VALUES ({bySystem}, now(), 'core.modules.changed', NULL, 'Система', {bySystem}, true),
                   ({byPerson}, now(), 'core.user.role.changed', {person}, 'Иван', {byPerson}, true)
            """);

        Guid systemJob = Guid.NewGuid(), personJob = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO jobs ("Id", "CreatedAt", "UpdatedAt", "Kind", "Status", "TargetId", "Title", "UserId")
            VALUES ({systemJob}, now(), now(), 'CreateBackup', 'Succeeded', {Guid.NewGuid()}, 'Копия', {Guid.Empty}),
                   ({personJob}, now(), now(), 'AssembleDocumentSet', 'Succeeded', {Guid.NewGuid()}, 'Сборка', {person})
            """);

        await db.Database.MigrateAsync();

        var channels = await db.ActivityRecords.AsNoTracking()
            .Where(r => r.Id == bySystem || r.Id == byPerson).ToDictionaryAsync(r => r.Id, r => r.Channel);
        Assert.Equal("system", channels[bySystem]);
        Assert.Equal("ui", channels[byPerson]);

        var jobs = await db.Jobs.AsNoTracking()
            .Where(j => j.Id == systemJob || j.Id == personJob).ToDictionaryAsync(j => j.Id, j => j.Channel);
        Assert.Equal("system", jobs[systemJob]);
        Assert.Equal("ui", jobs[personJob]);

        // Умолчание снято: новую строку без канала база не примет — канал даёт контекст действия, а не база.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO activity_log ("Id", "OccurredAt", "Action", "ActorName", "SessionId", "SeriesHead")
            VALUES ({Guid.NewGuid()}, now(), 'core.modules.changed', 'Система', {Guid.NewGuid()}, true)
            """));
        Assert.Equal(PostgresErrorCodes.NotNullViolation, ex.SqlState);
    }

    private Task DropAsync() => AdminAsync($"DROP DATABASE IF EXISTS {TestDatabases.Quote(_name)} WITH (FORCE)");

    private static async Task AdminAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(IntegrationTestFixture.TestConnectionString)
        {
            Database = "postgres",
            CommandTimeout = 300,
            Timeout = 60,
        }.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
