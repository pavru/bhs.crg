using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Maintenance;

/// <summary>
/// Разовый сбор реестра блобов по уже существующим данным (issue #672).
///
/// Зачем вообще. Проверка выдачи (<see cref="RegisteredBlobStorage" />) отказывает всему, чего нет в
/// реестре, а реестр наполняется только записью в хранилище — то есть сразу после выката ни один
/// ранее загруженный файл не открылся бы. Это не «желательный шаг миграции», а условие того, что
/// правка не ломает работающую систему.
///
/// <para><b>Строго ОДИН раз, EF-миграцией.</b> Первая версия запускала сбор на каждом старте, и это
/// было ошибкой замысла, а не оптимизации. Пути опознаются по форме строки, а JSONB реквизитов
/// пишет КЛИЕНТ (<c>PUT …/requisites</c> кладёт тело как есть). Повторяющийся сбор означал бы, что
/// пользователь вписывает строку нужного вида в любое текстовое поле, перезапуск заносит её в
/// реестр — и проверка начинает означать «строка совпала с выражением» вместо «объект создали мы»,
/// то есть ровно тот инвариант, ради которого всё затевалось. Разовый проход берёт данные,
/// накопленные ДО появления проверки, и на этом его работа кончается.</para>
///
/// <para><b>Почему EF-миграция допустима здесь</b>, хотя перенос картинок (<see cref="ImageBlobMigration" />)
/// сознательно ею не стал: тот ходит в блоб-хранилище, которое на старте может быть недоступно, а
/// этот работает чисто по БД. К моменту миграций база заведомо есть — иначе не применилась бы и сама
/// схема.</para>
///
/// <para><b>Почему SQL по <c>information_schema</c>, а не перечисление сущностей в C#.</b> Пути
/// лежат не только в отдельных колонках (их пять), но и внутри JSONB — путь обычного вложения живёт
/// в реквизитах, пути разрезанных PDF — в группировке файла набора. JSONB-колонок в схеме больше
/// тридцати, и список, выписанный руками, разошёлся бы с моделью при первой же новой колонке —
/// молча, ничего не сломав на вид.</para>
///
/// Класс остаётся отдельно от миграции, чтобы сбор можно было проверить тестами; идемпотентен
/// (<c>ON CONFLICT DO NOTHING</c> по уникальному пути).
/// </summary>
public class BlobRegistryBackfill(AppDbContext db, ILogger<BlobRegistryBackfill> log)
{
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var before = await db.BlobRegistry.CountAsync(ct);

        // Прямая команда, а не ExecuteSqlRawAsync: тот прогоняет строку через string.Format, и
        // фигурные скобки в SQL считает плейсхолдерами. Их здесь две группы, и обе обязательные —
        // '{}' в операторе #>> и счётчики повторов в регулярном выражении.
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = Sql;
            cmd.CommandTimeout = 600;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var added = await db.BlobRegistry.CountAsync(ct) - before;
        if (added > 0)
            log.LogInformation("Реестр блобов пополнен по существующим данным: {Added} путей", added);

        return added;
    }

    /// <remarks>
    /// Два прохода: по JSONB-колонкам (там живут вложения и картинки реквизитов) и по текстовым
    /// колонкам с путём в имени. Текстовые сознательно берём по имени, а не все подряд: иначе под
    /// выражение пришлось бы прогнать содержимое шаблонов и кэшей наборов — мегабайты ради пяти
    /// колонок.
    ///
    /// <para><b>Порядок отбора важен для стоимости.</b> Сначала грубый отбор СТРОК таблицы по тексту
    /// колонки, и только у прошедших разворачиваем JSON; выражение по пути стоит ВНУТРИ подзапроса,
    /// до <c>DISTINCT</c>. Иначе <c>DISTINCT</c> собирает все строковые узлы всех документов — а в
    /// <c>domain_objects.Data</c> это картинки в base64, и хеш-агрегат тянет их целиком.</para>
    ///
    /// <para>Имена таблиц в схеме — snake_case, имена колонок — PascalCase, поэтому <c>%I</c>: без
    /// кавычек Postgres сложил бы <c>Path</c> в <c>path</c>. Выражения подставляются через <c>%L</c>
    /// — как литералы, из одной константы <see cref="BlobPathShape" />, а не переписанные в SQL
    /// руками.</para>
    /// </remarks>
    // $$""" — не украшение: внутри лежит '{}' оператора #>>, и в обычной интерполируемой строке
    // одинарная скобка начала бы подстановку. С удвоенными разделителями подстановка — {{…}},
    // а одиночные скобки остаются текстом.
    public const string Sql = $$"""
        DO $$
        DECLARE
            r record;
            shape text := {{SqlShape}};
            rough text := {{SqlRough}};
        BEGIN
            FOR r IN
                SELECT c.table_name, c.column_name
                FROM information_schema.columns c
                JOIN information_schema.tables t
                  ON t.table_schema = c.table_schema AND t.table_name = c.table_name
                WHERE c.table_schema = 'public'
                  AND t.table_type = 'BASE TABLE'
                  AND c.data_type = 'jsonb'
            LOOP
                EXECUTE format(
                    'INSERT INTO blob_registry ("Id", "Path", "CreatedAt", "UpdatedAt")
                     SELECT gen_random_uuid(), s.p, now(), now()
                     FROM (
                         SELECT DISTINCT v #>> ''{}'' AS p
                         FROM %I x, LATERAL jsonb_path_query(x.%I, ''$.**'') v
                         WHERE x.%I::text ~ %L
                           AND jsonb_typeof(v) = ''string''
                           AND (v #>> ''{}'') ~ %L
                     ) s
                     ON CONFLICT ("Path") DO NOTHING',
                    r.table_name, r.column_name, r.column_name, rough, shape);
            END LOOP;

            FOR r IN
                SELECT c.table_name, c.column_name
                FROM information_schema.columns c
                JOIN information_schema.tables t
                  ON t.table_schema = c.table_schema AND t.table_name = c.table_name
                WHERE c.table_schema = 'public'
                  AND t.table_type = 'BASE TABLE'
                  AND c.data_type IN ('text', 'character varying')
                  AND c.column_name ILIKE '%BlobPath%'
                  AND c.table_name <> 'blob_registry'
            LOOP
                EXECUTE format(
                    'INSERT INTO blob_registry ("Id", "Path", "CreatedAt", "UpdatedAt")
                     SELECT gen_random_uuid(), s.p, now(), now()
                     FROM (SELECT DISTINCT x.%I AS p FROM %I x WHERE x.%I ~ %L) s
                     ON CONFLICT ("Path") DO NOTHING',
                    r.column_name, r.table_name, r.column_name, shape);
            END LOOP;
        END $$;
        """;

    /// <summary>Выражения — литералами SQL, чтобы форма пути не переписывалась в двух видах.</summary>
    private const string SqlShape = $"'{BlobPathShape.Pattern}'";
    private const string SqlRough = $"'{BlobPathShape.RoughPattern}'";
}
