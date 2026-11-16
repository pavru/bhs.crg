using BHS.CRG.Modules.Work.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Миграция <c>SubmissionWindow</c> модуля <c>work</c> (change <c>work-15-submission</c>, задача 2.1; ТЗ WORK-52): у
/// отчётов, заведённых до неё, окно подачи считается в самой миграции по нынешнему поясу — стройки, иначе компании — и
/// не остаётся пустым, иначе автоподача подала бы их моментом «начала времён».
///
/// <para>Своя база, доведённая до миграции ПЕРЕД этой: на базе прогона колонка уже стоит. Из таблиц ядра нужны две —
/// стройки и настройки, — и они заводятся здесь же узкой копией: миграция читает только пояс стройки и пояс
/// компании.</para>
/// </summary>
public class SubmissionWindowMigrationTests : IAsyncLifetime
{
    private readonly string _name = TestDatabases.Name("work_window");

    private static readonly Guid Moscow = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Vladivostok = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid WindowsZone = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid UnknownZone = Guid.Parse("00000000-0000-0000-0000-000000000004");

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
    public async Task Окно_прежних_черновиков_по_поясу_стройки_и_компании()
    {
        await SqlAsync("""
            CREATE TABLE public.constructions ("Id" uuid PRIMARY KEY, "TimeZoneId" varchar(128));
            CREATE TABLE public.app_settings ("Key" varchar(64) PRIMARY KEY, "Value" varchar(16384) NOT NULL);
            INSERT INTO public.app_settings VALUES ('company.timezone', 'Asia/Yekaterinburg');
            """);
        await SqlAsync($"""
            INSERT INTO public.constructions VALUES
                ('{Moscow}', 'Europe/Moscow'), ('{Vladivostok}', 'Asia/Vladivostok'), ('{WindowsZone}', 'Russian Standard Time'),
                ('{UnknownZone}', 'Nowhere/Zone');
            """);
        await using (var db = Context())
        {
            var all = db.Database.GetMigrations().ToList();
            await db.GetService<IMigrator>().MigrateAsync(all[all.IndexOf(all.Single(m => m.EndsWith("_SubmissionWindow"))) - 1]);
        }
        var day = Report(Moscow, "day");
        var night = Report(Vladivostok, "night");
        var windows = Report(WindowsZone, "day");
        var inherited = Report(UnknownZone, "day");
        await SqlAsync($"""
            INSERT INTO work.reports (id, employee_id, construction_id, date, shift, status, seq, created_at, updated_at)
            VALUES {day.Values}, {night.Values}, {windows.Values}, {inherited.Values};
            """);

        await using (var db = Context()) await db.Database.MigrateAsync();

        Assert.Equal(new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.FromHours(3)), await WindowAsync(day.Id));
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.FromHours(10)), await WindowAsync(night.Id));
        // Имя Windows — пояс самой стройки, как у порта строек (SiteZones), а не пояс компании.
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.FromHours(3)), await WindowAsync(windows.Id));
        // Пояса не знает никто — пояс компании.
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.FromHours(5)), await WindowAsync(inherited.Id));
    }

    /// <summary>Пересозданная база — миграции модуля проходят до конца и без таблиц ядра.</summary>
    [Fact]
    public async Task На_пустой_базе_миграции_применяются()
    {
        await using var db = Context();
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    private static (Guid Id, string Values) Report(Guid site, string shift)
    {
        var id = Guid.NewGuid();
        return (id, $"('{id}', '{Guid.NewGuid()}', '{site}', '2026-11-16', '{shift}', 'draft', 1, now(), now())");
    }

    private async Task<DateTimeOffset> WindowAsync(Guid id)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT window_ends_at FROM work.reports WHERE id = '{id}'", conn);
        return new DateTimeOffset((DateTime)(await cmd.ExecuteScalarAsync())!);
    }

    private string ConnectionString => new NpgsqlConnectionStringBuilder(IntegrationTestFixture.TestConnectionString)
    {
        Database = _name,
        CommandTimeout = 300,
        // База пересоздаётся на каждый тест: соединение из пула прежней базы мертво после DROP … WITH (FORCE).
        Pooling = false,
    }.ConnectionString;

    private WorkDbContext Context()
    {
        var options = new DbContextOptionsBuilder<WorkDbContext>();
        WorkDbContext.Configure(options, ConnectionString);
        return new WorkDbContext(options.Options);
    }

    private async Task SqlAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
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
