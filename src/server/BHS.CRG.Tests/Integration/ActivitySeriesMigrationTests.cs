using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Миграция <c>ActivitySeries</c> (ТЗ CORE-28.2): запись, сделанная до появления сессий, становится
/// одиночным рядом — своя сессия, сама себе голова.
///
/// <para>Проверяется на своей базе, доведённой до миграции ПЕРЕД этой: только там есть строка журнала
/// без новых колонок. На базе прогона миграция уже прошла по пустой таблице и не проверила бы ничего.
/// Без значения из миграции колонки получили бы умолчания EF — нулевой идентификатор и «не голова», и
/// вся прежняя история пропала бы с экрана журнала: страница идёт по головам.</para>
/// </summary>
public class ActivitySeriesMigrationTests : IAsyncLifetime
{
    private readonly string _name = TestDatabases.Name("activity_series");

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
    public async Task Прежняя_запись_журнала_становится_одиночной_головой()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(IntegrationTestFixture.TestConnectionString)
            {
                Database = _name,
                CommandTimeout = 300,
            }.ConnectionString)
            .Options);

        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_ActivitySeries", StringComparison.Ordinal));
        Assert.True(index > 0, "Миграции ActivitySeries в сборке нет — тест проверяет не то, что назвал.");
        await db.GetService<IMigrator>().MigrateAsync(all[index - 1]);

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO activity_log ("Id", "OccurredAt", "Action", "ActorId", "ActorName", "TargetId", "After")
            VALUES ({id}, now(), 'core.type.schema.changed', NULL, 'Система', 'тип', 'поля: номер')
            """);

        await db.Database.MigrateAsync();

        var record = await db.ActivityRecords.AsNoTracking().SingleAsync(r => r.Id == id);
        Assert.Equal(id, record.SessionId);
        Assert.True(record.SeriesHead);
        Assert.Equal("поля: номер", record.After);
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
