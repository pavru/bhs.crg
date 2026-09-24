using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Миграция не теряет справочник ядра — на ПЕРЕСОЗДАННОЙ базе и на базе С ИСТОРИЕЙ
/// (ТЗ CORE-29/CORE-31, issue #960).
///
/// <para>Две базы, а не одна, и это не педантизм: пересозданная приходит к последней миграции одним
/// прыжком, а у заказчика база проходит их подряд, поверх данных, накопленных прежними версиями.
/// Различает их порядок создания — и всё, что от него зависит: значения, проставленные прежними
/// умолчаниями, строки, которых в свежей схеме не бывает, и индексы, которые на пустой таблице
/// создаются всегда, а на полной могут и не создаться.</para>
///
/// <para>⚠️ Базы свои, а не общая тестовая: тест применяет миграции ЧАСТЯМИ и создаёт схему с нуля,
/// то есть делает с базой то, чего соседние тесты не переживут.</para>
/// </summary>
[Collection("Integration")]
public class MigrationCensusTests
{
    [Fact]
    public async Task Fresh_database_migrates_and_the_census_has_nothing_to_compare()
    {
        await using var db = await CreateDatabaseAsync("bhs_crg_census_fresh");

        // База пустая: таблиц нет, сверять не с чем — и это НЕ повод для отказа старта.
        var before = await MigrationCensus.ReadAsync(db);
        Assert.Null(before);

        await db.Database.MigrateAsync();

        var after = await MigrationCensus.ReadAsync(db);
        Assert.NotNull(after);
        Assert.Equal(0, after.Constructions);
        MigrationCensus.EnsureUnchanged(before, after);   // не бросает
    }

    /// <summary>
    /// ЧИСТАЯ УСТАНОВКА: базы ещё нет вовсе, её создаст первая миграция. Перепись обязана молча
    /// вернуть «нечего сверять», а не уронить старт.
    ///
    /// <para>Написан по следам отказа CI на PR #1046: сторож, поставленный ПЕРЕД миграцией, падал с
    /// «database does not exist» — то есть install.sh у заказчика не поднялся бы вовсе. Локально
    /// этого не видно никогда: база разработчика существует всегда, и проверка проходит вхолостую.</para>
    /// </summary>
    [Fact]
    public async Task Missing_database_is_not_a_failure_it_is_the_first_start()
    {
        var name = "bhs_crg_census_absent";
        await using (var conn = new NpgsqlConnection(AdminConnectionString()))
        {
            await conn.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", conn);
            await drop.ExecuteNonQueryAsync();
        }

        await using var db = new AppDbContext(OptionsFor(name));
        Assert.Null(await MigrationCensus.ReadAsync(db));
        MigrationCensus.EnsureUnchanged(null, null);   // и сверять нечего — не бросает
    }

    [Fact]
    public async Task Database_with_history_keeps_every_construction_section_and_set()
    {
        await using var db = await CreateDatabaseAsync("bhs_crg_census_history");

        // Схема ПРЕЖНЕЙ версии: доходим до предпоследней миграции — проверяем ту, что добавлена
        // последней, какой бы она ни была. Имя не прибито нарочно: прибитое устареет со следующей
        // миграцией, и тест продолжил бы проверять давно проверенное, молча.
        var all = db.Database.GetMigrations().ToList();
        var previous = all[^2];
        await db.GetService<IMigrator>().MigrateAsync(previous);

        // Данные, накопленные прежней версией: стройка → раздел → комплект → документ на комплекте.
        var construction = Guid.NewGuid();
        var section = Guid.NewGuid();
        var set = Guid.NewGuid();
        await ExecAsync(db, $"""
            INSERT INTO constructions ("Id","Name","CreatedByUserId","CreatedAt","UpdatedAt")
              VALUES ('{construction}','Стройка из прошлого','{Guid.NewGuid()}', now(), now());
            INSERT INTO sections ("Id","Name","ConstructionId","CreatedAt","UpdatedAt")
              VALUES ('{section}','Раздел','{construction}', now(), now());
            INSERT INTO document_sets ("Id","Name","SectionId","CreatedAt","UpdatedAt")
              VALUES ('{set}','Комплект','{section}', now(), now());
            """);

        var before = await MigrationCensus.ReadAsync(db);
        Assert.NotNull(before);
        Assert.Equal(1, before.Constructions);

        await db.Database.MigrateAsync();

        var after = await MigrationCensus.ReadAsync(db);
        MigrationCensus.EnsureUnchanged(before, after);

        // Колонки появились и пусты: «как у компании» — это отсутствие своего пояса, а не значение,
        // записанное миграцией в каждую строку (иначе смена пояса компании не тронула бы никого).
        Assert.Null(await ScalarAsync(db, $"SELECT \"TimeZoneId\" FROM constructions WHERE \"Id\" = '{construction}'"));
        Assert.Equal("Стройка из прошлого",
            await ScalarAsync(db, $"SELECT \"Name\" FROM constructions WHERE \"Id\" = '{construction}'"));
    }

    /// <summary>
    /// Сверка обязана ЛОВИТЬ потерю, а не только не мешать. Проверяется на переписях напрямую:
    /// подделать миграцию нельзя, а потеря выглядит именно так.
    /// </summary>
    [Fact]
    public void Census_refuses_when_something_disappeared()
    {
        var before = new MigrationCensus(2, 5, 4, 0, 0, 0);

        var lost = Assert.Throws<InvalidOperationException>(
            () => MigrationCensus.EnsureUnchanged(before, new MigrationCensus(2, 4, 4, 0, 0, 0)));
        Assert.Contains("разделов: было 5, стало 4", lost.Message);

        // Ссылка обрублена: строки на месте, но раздел больше не находит свою стройку. По итогам
        // это невидимо — ловится только счётом сирот.
        var orphaned = Assert.Throws<InvalidOperationException>(
            () => MigrationCensus.EnsureUnchanged(before, new MigrationCensus(2, 5, 4, 3, 0, 0)));
        Assert.Contains("разделов без своей стройки: было 0, стало 3", orphaned.Message);

        var wiped = Assert.Throws<InvalidOperationException>(
            () => MigrationCensus.EnsureUnchanged(before, null));
        Assert.Contains("справочник исчез целиком", wiped.Message);

        // А равные переписи проходят молча.
        MigrationCensus.EnsureUnchanged(before, new MigrationCensus(2, 5, 4, 0, 0, 0));
    }

    private static async Task<AppDbContext> CreateDatabaseAsync(string name)
    {
        await using (var conn = new NpgsqlConnection(AdminConnectionString()))
        {
            await conn.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", conn);
            await drop.ExecuteNonQueryAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", conn);
            await create.ExecuteNonQueryAsync();
        }

        return new AppDbContext(OptionsFor(name));
    }

    /// <summary>
    /// Подключение к служебной базе — для DROP/CREATE. Таймаут поднят: эти команды ждут, пока
    /// сервер освободится, а рядом идёт остальной прогон; тридцати секунд по умолчанию не хватало,
    /// и отказ приходил «таймаутом чтения» — виноватой выглядела база, а не теснота.
    /// </summary>
    private static string AdminConnectionString() =>
        new NpgsqlConnectionStringBuilder(IntegrationTestFixture.TestConnectionString)
        {
            Database = "postgres",
            CommandTimeout = 300,
            Timeout = 60,
        }.ConnectionString;

    /// <summary>Схема с нуля — это десятки миграций подряд, отсюда тот же поднятый таймаут.</summary>
    private static DbContextOptions<AppDbContext> OptionsFor(string database) =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(IntegrationTestFixture.TestConnectionString)
            {
                Database = database,
                CommandTimeout = 300,
                Timeout = 60,
            }.ConnectionString)
            .Options;

    private static async Task ExecAsync(AppDbContext db, string sql) =>
        await db.Database.ExecuteSqlRawAsync(sql);

    private static async Task<object?> ScalarAsync(AppDbContext db, string sql)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }
}
