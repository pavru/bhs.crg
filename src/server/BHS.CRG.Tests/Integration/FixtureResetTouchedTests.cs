using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Очистка между тестами усекает только ТРОНУТЫЕ таблицы (issue #1164) — и это обязано быть
/// неотличимо от усечения всех.
///
/// <para>Ошибка здесь выглядела бы не как отказ, а как плавающий тест в чужом классе: строка или
/// мёртвая версия, пережившая очистку, достаётся следующему. Поэтому проверяется не «очистка
/// прошла», а сам признак — на случаях, где «строк нет» и «не трогали» расходятся.</para>
/// </summary>
[Collection("Integration")]
public class FixtureResetTouchedTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    /// <summary>Таблица попроще: три столбца, ни одной ссылки.</summary>
    private const string Table = "app_settings";

    private const string Insert =
        """INSERT INTO app_settings ("Key", "Value", "UpdatedAt") VALUES ('touched-probe', 'x', now())""";

    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task After_reset_no_table_is_touched()
    {
        await WithDb(async db => Assert.Empty(await IntegrationTestFixture.TouchedTablesAsync(db)));
    }

    [Fact]
    public async Task Inserted_row_marks_its_table_and_only_it()
    {
        await WithDb(async db =>
        {
            await db.Database.ExecuteSqlRawAsync(Insert);
            Assert.Equal([Table], await IntegrationTestFixture.TouchedTablesAsync(db));
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
            Assert.Equal([Table], await IntegrationTestFixture.TouchedTablesAsync(db));
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
            Assert.Equal([Table], await IntegrationTestFixture.TouchedTablesAsync(db));
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
            Assert.Empty(await IntegrationTestFixture.TouchedTablesAsync(db));
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
