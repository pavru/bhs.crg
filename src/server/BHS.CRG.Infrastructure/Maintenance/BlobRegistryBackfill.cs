using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Maintenance;

/// <summary>
/// Разовый сбор реестра блобов по уже существующим данным (issue #672).
///
/// Зачем вообще. Проверка выдачи (<c>RegisteredBlobStorage</c>) отказывает всему, чего нет в
/// реестре, а реестр наполняется только записью в хранилище — то есть сразу после выката ни один
/// ранее загруженный файл не открылся бы. Это не «желательный шаг миграции», а условие того, что
/// правка не ломает работающую систему.
///
/// <para><b>Почему по БД, а не по хранилищу.</b> Перечислить объекты бакета было бы короче, но
/// разовые переносы в этом проекте сознательно не завязывают на хранилище: оно на момент старта
/// может быть недоступно (тот же довод записан в <see cref="ImageBlobMigration" />). Здесь это
/// важнее обычного — сбор идёт на старте, до первого запроса, и падение из-за недоступного MinIO
/// означало бы, что приложение не поднимается.</para>
///
/// <para><b>Почему SQL по <c>information_schema</c>, а не перечисление сущностей в C#.</b> Пути
/// лежат не только в отдельных колонках (их пять), но и внутри JSONB — путь обычного вложения
/// живёт в реквизитах, куда его кладёт клиент, пути разрезанных PDF — в группировке файла набора.
/// JSONB-колонок в схеме больше тридцати, и список, выписанный руками, разойдётся с моделью при
/// первой же новой колонке — молча, ничего не сломав на вид. Здесь колонки берутся из самой базы,
/// поэтому сбор покрывает и те, которых ещё нет.</para>
///
/// Идемпотентен: повторный прогон ничего не добавляет (<c>ON CONFLICT DO NOTHING</c> по
/// уникальному пути).
/// </summary>
public class BlobRegistryBackfill(AppDbContext db, ILogger<BlobRegistryBackfill> log)
{
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var before = await db.BlobRegistry.CountAsync(ct);

        // Прямая команда, а не ExecuteSqlRawAsync: тот прогоняет строку через string.Format, и
        // фигурные скобки в SQL считает плейсхолдерами. Их здесь две группы, и обе обязательные —
        // '{}' в операторе #>> и счётчики повторов в регулярном выражении ({4}, {36}).
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = Sql;
            cmd.CommandTimeout = 300;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var added = await db.BlobRegistry.CountAsync(ct) - before;
        if (added > 0)
            log.LogInformation("Реестр блобов пополнен по существующим данным: {Added} путей", added);

        return added;
    }

    /// <remarks>
    /// Два прохода: по JSONB-колонкам (там живут вложения и картинки реквизитов) и по текстовым
    /// колонкам с путём в имени (<c>BlobPath</c>, <c>ScanBlobPath</c>). Текстовые сознательно берём
    /// по имени, а не все подряд: иначе под регулярное выражение пришлось бы прогнать содержимое
    /// шаблонов и кэшей наборов данных — мегабайты ради колонок, которых пять.
    ///
    /// <para>Путь опознаём по ФОРМЕ (<c>{бакет}/{дата}/{guid}_{имя}</c>), а не по имени ключа
    /// JSON: имена ключей задаёт клиент и они разные в разных местах (<c>blobPath</c>,
    /// <c>scanBlobPath</c>, <c>originalBlobPath</c>, <c>src</c>), а форму задаёт
    /// <c>MinIOBlobStorage.UploadAsync</c> в одном месте на всё приложение. Регулярное выражение
    /// заключено в долларовые кавычки (<c>$re$</c>) — внутри строки, которую собирает
    /// <c>format</c>, обычные кавычки пришлось бы удваивать дважды.</para>
    ///
    /// <para><b>Разделитель даты допускаем любой</b> (<c>.</c>, <c>/</c>, <c>-</c>) — и это не
    /// перестраховка. <c>UploadAsync</c> строит дату форматом <c>yyyy/MM/dd</c>, где <c>/</c> в .NET
    /// означает не символ, а ПЛЕЙСХОЛДЕР разделителя даты текущей культуры: под русской локалью он
    /// разворачивается в точку, и в базе лежит <c>bhs-crg/2026.06.25/…</c>. Первая версия этого
    /// сбора требовала слэшей и на живых данных не нашла ни одного пути из полусотни.</para>
    ///
    /// <para>Имена таблиц в схеме — snake_case, имена колонок — PascalCase, поэтому <c>%I</c>:
    /// без кавычек Postgres сложил бы <c>Path</c> в <c>path</c>. И DISTINCT берётся по одному пути
    /// во вложенном запросе, а не по строке целиком: <c>gen_random_uuid()</c> в списке выборки делает
    /// различными любые две строки, и DISTINCT снаружи не свернул бы повторы.</para>
    /// </remarks>
    private const string Sql = """
        DO $$
        DECLARE
            r record;
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
                         WHERE x.%I IS NOT NULL AND jsonb_typeof(v) = ''string''
                     ) s
                     WHERE s.p ~ $re$^[^/]+/[0-9]{4}[./-][0-9]{2}[./-][0-9]{2}/[0-9a-fA-F-]{36}_$re$
                     ON CONFLICT ("Path") DO NOTHING',
                    r.table_name, r.column_name, r.column_name);
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
                     FROM (SELECT DISTINCT x.%I AS p FROM %I x WHERE x.%I IS NOT NULL) s
                     WHERE s.p ~ $re$^[^/]+/[0-9]{4}[./-][0-9]{2}[./-][0-9]{2}/[0-9a-fA-F-]{36}_$re$
                     ON CONFLICT ("Path") DO NOTHING',
                    r.column_name, r.table_name, r.column_name);
            END LOOP;
        END $$;
        """;
}
