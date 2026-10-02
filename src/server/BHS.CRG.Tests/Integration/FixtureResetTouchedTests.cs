using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Очистка между тестами усекает только ТРОНУТЫЕ таблицы (issue #1164) — и это обязано быть
/// неотличимо от усечения всех.
///
/// <para>Ошибка здесь выглядела бы не как отказ, а как плавающий тест в чужом классе: строка или
/// мёртвая версия, пережившая очистку, достаётся следующему. Поэтому проверяется не «очистка
/// прошла», а сам признак — на случаях, где «строк нет» и «не трогали» расходятся.</para>
///
/// <para>Утверждения — про таблицы, которые трогает сама проверка, а не про «в базе тронуто ровно
/// это»: база здесь общая с работающим хостом, и его собственная запись в журнал уронила бы
/// проверку, ничего не сказав о признаке.</para>
/// </summary>
[Collection("Integration")]
public class FixtureResetTouchedTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    /// <summary>Таблица попроще: три столбца, ни одной ссылки.</summary>
    private const string Table = "app_settings";

    /// <summary>Вторая таблица проверок: в неё пишет только <c>Reset_empties…</c>.</summary>
    private const string Neighbour = "service_state";

    private const string Insert =
        """INSERT INTO app_settings ("Key", "Value", "UpdatedAt") VALUES ('touched-probe', 'x', now())""";

    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task After_reset_the_probe_table_is_not_touched()
    {
        await WithDb(async db => Assert.DoesNotContain(Table, await IntegrationTestFixture.TouchedTablesAsync(db)));
    }

    [Fact]
    public async Task Inserted_row_marks_its_table_and_not_its_neighbour()
    {
        await WithDb(async db =>
        {
            await db.Database.ExecuteSqlRawAsync(Insert);

            var touched = await IntegrationTestFixture.TouchedTablesAsync(db, [Table, Neighbour]);
            Assert.Equal([Table], touched);
        });
    }

    /// <summary>
    /// Ради этого признак и взят по размеру: строк в таблице нет, а мёртвая версия в куче лежит.
    /// Спроси мы «есть ли строки», таблица осталась бы неусечённой.
    /// </summary>
    [Fact]
    public async Task Deleted_row_still_marks_its_table()
    {
        await WithDb(async db =>
        {
            await db.Database.ExecuteSqlRawAsync(Insert);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM app_settings");

            Assert.Equal(0, await CountAsync(db));
            Assert.Contains(Table, await IntegrationTestFixture.TouchedTablesAsync(db));
        });
    }

    /// <summary>Откат строку убирает, а страницу не отдаёт — версия остаётся в куче.</summary>
    [Fact]
    public async Task Rolled_back_insert_still_marks_its_table()
    {
        await WithDb(async db =>
        {
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                await db.Database.ExecuteSqlRawAsync(Insert);
                await tx.RollbackAsync();
            }

            Assert.Equal(0, await CountAsync(db));
            Assert.Contains(Table, await IntegrationTestFixture.TouchedTablesAsync(db));
        });
    }

    [Fact]
    public async Task Reset_empties_what_was_touched_and_leaves_nothing_marked()
    {
        await WithDb(async db =>
        {
            await db.Database.ExecuteSqlRawAsync(Insert);
            // Фигурные скобки удвоены: ExecuteSqlRaw читает одиночные как место для параметра.
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO service_state ("Id", "Key", "Data", "CreatedAt", "UpdatedAt")
                VALUES (gen_random_uuid(), 'touched-probe', '{{}}', now(), now())
                """);
        });

        await fixture.ResetDatabaseAsync();

        await WithDb(async db =>
        {
            Assert.Equal(0, await CountAsync(db));
            Assert.Equal(0, await db.Database.SqlQueryRaw<int>(
                """SELECT count(*)::int AS "Value" FROM service_state""").SingleAsync());
            Assert.Empty(await IntegrationTestFixture.TouchedTablesAsync(db, [Table, Neighbour]));
        });
    }

    /// <summary>
    /// Очистка дожидается транзакции, которую не закрыл прошлый тест, и сносит её запись — даже
    /// когда на момент очистки усекать ещё нечего.
    ///
    /// <para>Раньше это давал сам TRUNCATE всех таблиц. Усечение одних тронутых без замка это
    /// теряет: таблица на момент вопроса чиста, усечения нет, очистка возвращается сразу, а запись
    /// приходит следом и достаётся следующему тесту.</para>
    ///
    /// <para>Ждём не время, а событие: очередь за замком видна в <c>pg_locks</c>. Смотрим на неё с
    /// отдельного соединения — то, что держит транзакцию, занято ею.</para>
    /// </summary>
    [Fact]
    public async Task Reset_waits_for_a_straggling_transaction_and_wipes_its_row()
    {
        await using var straggler = new NpgsqlConnection(IntegrationTestFixture.TestConnectionString);
        await straggler.OpenAsync();
        await using var observer = new NpgsqlConnection(IntegrationTestFixture.TestConnectionString);
        await observer.OpenAsync();

        Task reset;
        await using (var tx = await straggler.BeginTransactionAsync())
        {
            // Чтение, а не запись: таблица остаётся нетронутой, но замок на неё уже взят.
            await using (var read = new NpgsqlCommand("SELECT count(*) FROM app_settings", straggler, tx))
                await read.ExecuteScalarAsync();

            reset = fixture.ResetDatabaseAsync();

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            for (;;)
            {
                await using var waiting = new NpgsqlCommand(
                    "SELECT count(*) FROM pg_locks WHERE NOT granted AND relation = 'app_settings'::regclass",
                    observer);
                if ((long)(await waiting.ExecuteScalarAsync())! > 0) break;

                Assert.False(reset.IsCompleted,
                    "очистка завершилась, не дождавшись открытой транзакции: её запись переживёт очистку");
                Assert.True(DateTime.UtcNow < deadline, "очистка не встала в очередь за замком за 10 с");
                await Task.Delay(20);
            }

            await using (var write = new NpgsqlCommand(Insert, straggler, tx))
                await write.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        }

        await reset;
        await WithDb(async db => Assert.Equal(0, await CountAsync(db)));
    }

    /// <summary>
    /// У секционированной таблицы свой файл пуст всегда: данные лежат в секциях. Признак по размеру
    /// одного родителя не увидел бы её никогда — и таблица, переведённая на секции, молча перестала
    /// бы очищаться.
    ///
    /// <para>Таблица заводится здесь же и здесь же сносится: в модели секционированных нет, а
    /// проверить признак больше не на чем.</para>
    /// </summary>
    [Fact]
    public async Task Partitioned_table_is_touched_when_its_partition_is()
    {
        const string parent = "touched_probe_parted";
        await WithDb(async db =>
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                DROP TABLE IF EXISTS touched_probe_parted;
                CREATE TABLE touched_probe_parted (id int NOT NULL) PARTITION BY RANGE (id);
                CREATE TABLE touched_probe_parted_1 PARTITION OF touched_probe_parted FOR VALUES FROM (0) TO (100);
                """);
            try
            {
                Assert.Empty(await IntegrationTestFixture.TouchedTablesAsync(db, [parent]));

                await db.Database.ExecuteSqlRawAsync("INSERT INTO touched_probe_parted (id) VALUES (1)");
                Assert.Equal([parent], await IntegrationTestFixture.TouchedTablesAsync(db, [parent]));
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS touched_probe_parted");
            }
        });
    }

    /// <summary>
    /// На этом держится равенство «усечь тронутые» и «усечь все»: CASCADE не должен дотягиваться за
    /// пределы списка. Сошлись бы таблица, оставляемая между тестами, на усекаемую — CASCADE сносил
    /// бы оставленное, и раньше это случалось бы на каждой очистке, а теперь — только когда
    /// усекаемая тронута: тот же дефект, но плавающий.
    ///
    /// <para>Смотрим только схему ядра: таблицы модулей усекаются целиком следом, тем же вызовом
    /// (<see cref="IntegrationTestFixture.ResetModuleSchemasAsync" />), и CASCADE до них ничего не
    /// меняет.</para>
    /// </summary>
    [Fact]
    public async Task No_table_outside_the_list_references_a_truncated_one()
    {
        await WithDb(async db =>
        {
            var strangers = await db.Database
                .SqlQueryRaw<string>(
                    """
                    SELECT c.conrelid::regclass::text || ' → ' || c.confrelid::regclass::text AS "Value"
                    FROM pg_constraint c
                    JOIN pg_class child ON child.oid = c.conrelid
                    JOIN pg_class parent ON parent.oid = c.confrelid
                    WHERE c.contype = 'f'
                      AND child.relnamespace = 'public'::regnamespace
                      AND parent.relname = ANY({0})
                      AND NOT child.relname = ANY({0})
                    """,
                    (object)IntegrationTestFixture.TruncatedTables)
                .ToListAsync();

            Assert.True(strangers.Count == 0,
                "На усекаемые таблицы ссылаются таблицы вне списка очистки: " + string.Join(", ", strangers) +
                ". CASCADE снесёт их вместе с усекаемой — добавьте их в TruncatedTables или уберите ссылку.");
        });
    }

    private static Task<int> CountAsync(AppDbContext db) =>
        db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM app_settings""").SingleAsync();

    private async Task WithDb(Func<AppDbContext, Task> act)
    {
        using var scope = fixture.Services.CreateScope();
        await act(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}
