using BHS.CRG.Domain.Storage;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Persistence.Configurations;
using BHS.CRG.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Maintenance;

/// <summary>
/// Разовый сбор владельцев реестра блобов для файлов, лежавших до колонки владельца (ТЗ CORE-37.2).
///
/// <para>Колонка появляется с владельцем «ядро» у всех строк — и сразу после выката скан счёта
/// перестал бы открываться адресом модуля: порт <c>costs</c> читает только файлы <c>costs</c>. Сбор
/// возвращает модулю его файлы, и без него переход ломал бы работающую систему.</para>
///
/// <para><b>Чей путь.</b> Путь, найденный в колонке схемы модуля, — файл этого модуля; остальные
/// остаются ядру. Схема называется кодом модуля либо кодом и подчёркиванием (<c>ModuleSchema.Validate</c>),
/// поэтому код — часть имени до первого подчёркивания; кодов с подчёркиванием среди модулей нет.</para>
///
/// <para><b>Какая схема — модуля.</b> Та, где лежит история миграций EF: модуль обязан держать её в
/// своей схеме и под стандартным именем (<c>ModuleSchemaMigrator.EnsureHistoryStaysInSchema</c>), а
/// ядро держит свою в <c>public</c>. Списка модулей у миграции нет — она статична, — а брать любую
/// непубличную схему нельзя: схема расширения или оставленная руками отдала бы «владельцу» со своим
/// именем вложения ядра, и вернуть их было бы нечем — сбор разовый.
/// Образ файла (<c>renditions</c>) лежит в таблице ЯДРА, но принадлежит владельцу оригинала: модуль
/// открывает образ своего скана своим портом.</para>
///
/// <para><b>Строго ОДИН раз, EF-миграцией</b> — по той же причине, что сбор самого реестра
/// (<see cref="BlobRegistryBackfill" />): повторный проход по данным, которые пишет клиент, позволил бы
/// присвоить чужой файл. Здесь это видно прямо: путь, вписанный в поле модуля, отдал бы файл модулю.
/// Разовый проход берёт данные, накопленные ДО появления владельца, и на этом его работа кончается.
/// Класс отдельно от миграции — чтобы проверить сбор тестом; повтор ничего не меняет.</para>
///
/// <para>Колонки модуля берутся все, а не по имени: путь файла модуль хранит где хочет (резервная
/// копия находит его так же, <c>ModuleFileScan</c>), а таблицы модулей малы.</para>
/// </summary>
public class BlobRegistryOwnerBackfill(AppDbContext db, ILogger<BlobRegistryOwnerBackfill> log)
{
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var before = await db.BlobRegistry.CountAsync(e => e.Owner != BlobOwner.Core, ct);

        // Прямая команда, а не ExecuteSqlRawAsync, — по той же причине, что у сбора реестра: фигурные
        // скобки '{}' в SQL тот принял бы за плейсхолдеры.
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = Sql;
            cmd.CommandTimeout = 600;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var moved = await db.BlobRegistry.CountAsync(e => e.Owner != BlobOwner.Core, ct) - before;
        if (moved > 0)
            log.LogInformation("Владельцы реестра блобов собраны: файлов модулей {Moved}", moved);

        return moved;
    }

    /// <remarks>
    /// Два шага, и порядок важен: сначала файлы модулей по их колонкам, потом образы — по владельцу
    /// уже собранного оригинала.
    ///
    /// <para>Отбирается только «ядро»: файл, у которого владелец уже модуль, сбор не трогает, — так
    /// повтор ничего не меняет, а путь, найденный в схемах двух модулей (такого не бывает: путь у
    /// каждой загрузки новый), достаётся первому, а не последнему.</para>
    /// </remarks>
    // $$""" — по той же причине, что у BlobRegistryBackfill: '{}' оператора #>> внутри.
    public const string Sql = $$"""
        DO $$
        DECLARE
            r record;
            shape text := {{SqlShape}};
            rough text := {{SqlRough}};
        BEGIN
            FOR r IN
                SELECT c.table_schema, c.table_name, c.column_name, c.data_type,
                       split_part(c.table_schema, '_', 1) AS owner
                FROM information_schema.columns c
                JOIN information_schema.tables t
                  ON t.table_schema = c.table_schema AND t.table_name = c.table_name
                WHERE c.table_schema NOT IN ('public', 'information_schema')
                  AND c.table_schema NOT LIKE 'pg\_%'
                  AND EXISTS (SELECT 1 FROM information_schema.tables h
                              WHERE h.table_schema = c.table_schema AND h.table_name = '{{History}}')
                  AND t.table_type = 'BASE TABLE'
                  AND c.data_type IN ('jsonb', 'text', 'character varying')
                ORDER BY c.table_schema, c.table_name, c.column_name
            LOOP
                IF r.data_type = 'jsonb' THEN
                    EXECUTE format(
                        'UPDATE blob_registry b SET "Owner" = %L, "UpdatedAt" = now()
                         FROM (
                             SELECT DISTINCT v #>> ''{}'' AS p
                             FROM %I.%I x, LATERAL jsonb_path_query(x.%I, ''$.**'') v
                             WHERE x.%I::text ~ %L
                               AND jsonb_typeof(v) = ''string''
                               AND (v #>> ''{}'') ~ %L
                         ) s
                         WHERE b."Path" = s.p AND b."Owner" = %L',
                        r.owner, r.table_schema, r.table_name, r.column_name, r.column_name, rough, shape,
                        '{{BlobOwner.Core}}');
                ELSE
                    EXECUTE format(
                        'UPDATE blob_registry b SET "Owner" = %L, "UpdatedAt" = now()
                         FROM (SELECT DISTINCT x.%I AS p FROM %I.%I x WHERE x.%I ~ %L) s
                         WHERE b."Path" = s.p AND b."Owner" = %L',
                        r.owner, r.column_name, r.table_schema, r.table_name, r.column_name, shape,
                        '{{BlobOwner.Core}}');
                END IF;
            END LOOP;

            UPDATE blob_registry image SET "Owner" = original."Owner", "UpdatedAt" = now()
            FROM {{RenditionConfiguration.Table}} rendition
            JOIN blob_registry original ON original."Path" = rendition."{{RenditionConfiguration.OriginalColumn}}"
            WHERE image."Path" = rendition."{{nameof(RenditionRecord.ImageBlobPath)}}"
              AND image."Owner" = '{{BlobOwner.Core}}'
              AND original."Owner" <> '{{BlobOwner.Core}}';
        END $$;
        """;

    /// <summary>Таблица истории миграций под стандартным именем — признак схемы модуля.</summary>
    private const string History = HistoryRepository.DefaultTableName;

    private const string SqlShape = $"'{BlobPathShape.Pattern}'";
    private const string SqlRough = $"'{BlobPathShape.RoughPattern}'";
}
