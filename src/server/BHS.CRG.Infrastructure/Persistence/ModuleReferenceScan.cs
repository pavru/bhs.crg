using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace BHS.CRG.Infrastructure.Persistence;

/// <summary>Колонка вне ядра, в которой нашлись спрошенные идентификаторы.</summary>
/// <param name="Hits">Идентификатор → в скольких строках таблицы он стоит в этой колонке.</param>
/// <param name="Rows">Сколько строк таблицы держат хотя бы один из спрошенных идентификаторов. Не
/// сумма <paramref name="Hits" />: строка с двумя спрошенными идентификаторами — одна строка.</param>
public sealed record HeldColumn(
    string Schema, string Table, string Column, IReadOnlyDictionary<Guid, int> Hits, int Rows)
{
    public string Address => $"{Schema}.{Table}.{Column}";
}

/// <summary>Данные вне ядра прочитать не удалось — сказать «никто не держит» нельзя.</summary>
public sealed class ModuleDataUnreadableException(string address, PostgresException cause)
    : InvalidOperationException($"Не удалось прочитать {address}: {cause.MessageText}", cause)
{
    public string Address { get; } = address;
}

/// <summary>
/// Где ВНЕ ядра встречаются эти идентификаторы — по схеме базы (ТЗ CORE-34.1, задача G2,
/// issue #1094).
///
/// <para><b>Почему схема, а не объявления модулей.</b> Согласованная первой редакция спрашивала
/// колонки, которые модуль объявил. Объявление — список, выписанный руками, и расходится он с данными
/// молча и в сторону потери: схема ВЫКЛЮЧЕННОГО модуля отстаёт от его кода (колонку под прежним
/// именем объявление не нашло бы, и «нет столбца» значило бы «ссылок нет»), а у модуля, вынутого из
/// сборки, объявлений нет вовсе. Перепись в CI не ловит ни то, ни другое — она видит только нынешнюю
/// модель. Тот же довод, по которому держателей файлов ищет <c>LiveBlobPathScan</c> (PR #1183);
/// решение владельца от 04.10.2026, ревизия Архитектора того же дня.</para>
///
/// <para>Скан о модулях не знает ничего: отдаёт адрес колонки и числа. Чья это колонка, какими
/// словами её назвать и держит ли она вообще — знает тот, кто видит объявления (реализация порта
/// <c>IRecordHolders</c>).</para>
///
/// <para><b>Что читается.</b> Колонки-идентификаторы, их массивы и JSON — во всех схемах, кроме
/// ядра и служебных. Ложного совпадения здесь не бывает: идентификаторы ядра с идентификаторами
/// модуля не пересекаются, поэтому собственный ключ модуля не «держит» ничего, а общий с ядром ключ
/// (таблица-расширение «один к одному») — держит по праву. Не читаются только внешние ключи ВНУТРИ
/// своей схемы: там заведомо идентификаторы модуля, и пропускаются они ради цены, а не смысла.</para>
///
/// <para><b>Чего скан не видит.</b> Идентификатор в произвольном тексте и идентификатор в JSON,
/// записанный без дефисов. Текст отложен до замера на копии базы заказчика (сырой текст
/// распознавания под выражением может оказаться дорогим); вторая запись системой не порождается.</para>
///
/// <para>Список колонок берётся из каталога базы, а не из <c>information_schema</c>: та показывает
/// только колонки, на которые у роли есть хоть какое-то право, — то есть нечитаемая колонка могла бы
/// не попасть в список вовсе и сойти за «ссылок нет».</para>
/// </summary>
public class ModuleReferenceScan(AppDbContext db)
{
    private enum Kind { Uuid, UuidArray, Json }

    private readonly record struct RefColumn(string Schema, string Table, string Column, Kind Kind)
    {
        public string Address => $"{Schema}.{Table}.{Column}";
    }

    private const string ColumnsSql = """
        SELECT n.nspname, c.relname, a.attname,
               CASE t.typname WHEN 'uuid' THEN 0 WHEN '_uuid' THEN 1 ELSE 2 END AS kind
        FROM pg_attribute a
        JOIN pg_class c ON c.oid = a.attrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        JOIN pg_type t ON t.oid = a.atttypid
        WHERE a.attnum > 0 AND NOT a.attisdropped
          AND c.relkind IN ('r', 'p') AND NOT c.relispartition
          AND n.nspname NOT IN ('public', 'pg_catalog', 'information_schema')
          AND n.nspname NOT LIKE 'pg\_%'
          AND t.typname IN ('uuid', '_uuid', 'json', 'jsonb', '_json', '_jsonb')
          AND NOT EXISTS (
              SELECT 1 FROM pg_constraint k
              JOIN pg_class target ON target.oid = k.confrelid
              WHERE k.conrelid = c.oid AND k.contype = 'f'
                AND a.attnum = ANY(k.conkey)
                AND target.relnamespace = c.relnamespace)
        ORDER BY n.nspname, c.relname, a.attnum
        """;

    /// <summary>Идентификатор в тексте JSON — в привычной записи, строчными.</summary>
    private const string UuidInText = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";

    /// <summary>
    /// Колонки, где встречается хотя бы один из идентификаторов. Список колонок и чтение каждой —
    /// в одном снимке базы: иначе ссылка, переехавшая по ходу скана из непрочитанной колонки в
    /// прочитанную, не нашлась бы ни там, ни там.
    ///
    /// <para>⚠️ Снимок не останавливает запись модуля: ссылка, появившаяся ПОСЛЕ него, удалению не
    /// помешает и станет потерянной. Это названный источник потерь (ТЗ CORE-34.3), а не недосмотр:
    /// общей транзакции у ядра и модуля нет (CORE-4), и замок потребовал бы от модуля брать его при
    /// каждой записи ссылки — забытый вызов молчал бы.</para>
    /// </summary>
    /// <exception cref="ModuleDataUnreadableException">Колонку не удалось прочитать.</exception>
    public async Task<IReadOnlyList<HeldColumn>> FindAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];

        // Чужую транзакцию не трогаем: снимок в ней задаёт тот, кто её открыл. Но отказ базы внутри
        // неё прерывает транзакцию целиком — следующий запрос вызывающего упал бы «current transaction
        // is aborted», внутренней ошибкой вместо отказа (ревью PR #1188). Поэтому в чужой транзакции
        // скан идёт под точкой сохранения и откатывается к ней.
        var foreign = db.Database.CurrentTransaction;
        await using var snapshot = foreign is null
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct)
            : null;
        if (foreign is not null) await foreign.CreateSavepointAsync(Savepoint, ct);

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var address = "каталог базы";
        try
        {
            // Своя транзакция — свои пределы: миграция модуля держит таблицу исключительным замком, и
            // без предела удаление записи висело бы на нём, пока не истечёт таймаут запроса у клиента.
            // С пределом это отказ «повторите позже». В чужой транзакции пределы задаёт её хозяин.
            if (foreign is null)
                await ExecuteAsync(connection, $"SET LOCAL lock_timeout = '{LockTimeout}'; SET LOCAL statement_timeout = '{StatementTimeout}'", ct);

            var columns = new List<RefColumn>();
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = ColumnsSql;
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    columns.Add(new RefColumn(
                        reader.GetString(0), reader.GetString(1), reader.GetString(2), (Kind)reader.GetInt32(3)));
            }

            var asked = ids.Distinct().ToArray();
            var found = new List<HeldColumn>();
            foreach (var column in columns)
            {
                address = column.Address;
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = Sql(column, asked.Length);
                cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = asked });
                if (column.Kind == Kind.Json)
                {
                    cmd.Parameters.Add(new NpgsqlParameter("uuid", UuidInText));
                    if (asked.Length <= LikeUpTo)
                        cmd.Parameters.Add(new NpgsqlParameter("likes", asked.Select(id => $"%{id}%").ToArray()));
                }

                // Строка ответа — строка ТАБЛИЦЫ (или группа строк с одним значением) и те из спрошенных
                // идентификаторов, что в ней стоят. Считать приходится так, а не суммой по
                // идентификаторам: удаление уровня спрашивает о многих записях разом, и счёт, в чьих
                // полях стоят две из них, шёл бы в отказ как два (ревью PR #1188).
                var hits = new Dictionary<Guid, int>();
                var rows = 0;
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var inRow = reader.GetFieldValue<Guid[]>(0);
                    if (inRow.Length == 0) continue;

                    var count = reader.GetInt32(1);
                    rows += count;
                    foreach (var id in inRow) hits[id] = hits.GetValueOrDefault(id) + count;
                }

                if (rows > 0)
                    found.Add(new HeldColumn(column.Schema, column.Table, column.Column, hits, rows));
            }

            if (foreign is not null) await foreign.ReleaseSavepointAsync(Savepoint, ct);
            return found;
        }
        catch (PostgresException ex)
        {
            if (foreign is not null) await foreign.RollbackToSavepointAsync(Savepoint, ct);
            // Пропустить колонку нельзя: «не смог прочитать» превратилось бы в «никто не держит».
            throw new ModuleDataUnreadableException(address, ex);
        }
    }

    private const string Savepoint = "record_holders_scan";
    private const string LockTimeout = "5s";
    private const string StatementTimeout = "60s";

    /// <summary>
    /// До скольких идентификаторов строки JSON отбираются поиском подстроки, а не выражением. Одна
    /// запись — обычный случай (удаление записи, карточка типа), и подстрока на порядок дешевле
    /// разбора каждой строки выражением. Удаление уровня спрашивает о сотнях — там сотня подстрок на
    /// строку дороже одного выражения.
    /// </summary>
    private const int LikeUpTo = 20;

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Названия документов-держателей: «№ 12», «№ 15» — для колонки-идентификатора, у которой модуль
    /// объявил, чем назвать документ. Не больше <paramref name="limit" />: отказ — не отчёт.
    ///
    /// <para>Не удалось (схема отстала от объявления, колонка оказалась не идентификатором) — пустой
    /// список, а не отказ: число держателей уже посчитано сканом, названия — пояснение к нему. В
    /// чужой транзакции запрос идёт под точкой сохранения — по той же причине, что и скан.</para>
    /// </summary>
    public async Task<IReadOnlyList<string>> LabelsAsync(
        HeldColumn held, string documentTable, string documentKey, string labelColumn, string via,
        int limit, CancellationToken ct = default)
    {
        var foreign = db.Database.CurrentTransaction;
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await db.Database.OpenConnectionAsync(ct);
        if (foreign is not null) await foreign.CreateSavepointAsync(Savepoint, ct);
        try
        {
            var labels = new List<string>();
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT DISTINCT d.{Id(labelColumn)}::text
                    FROM {Id(held.Schema)}.{Id(held.Table)} r
                    JOIN {Id(held.Schema)}.{Id(documentTable)} d ON d.{Id(documentKey)} = r.{Id(via)}
                    WHERE r.{Id(held.Column)} = ANY(@ids) AND d.{Id(labelColumn)} IS NOT NULL
                    ORDER BY 1 LIMIT @limit
                    """;
                cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
                    { Value = held.Hits.Keys.ToArray() });
                cmd.Parameters.Add(new NpgsqlParameter("limit", limit));

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) labels.Add(reader.GetString(0));
            }

            if (foreign is not null) await foreign.ReleaseSavepointAsync(Savepoint, ct);
            return labels;
        }
        catch (PostgresException)
        {
            if (foreign is not null) await foreign.RollbackToSavepointAsync(Savepoint, ct);
            return [];
        }
        finally
        {
            if (opened) await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Запрос по колонке. Форма ответа у всех видов одна: спрошенные идентификаторы, стоящие в строке,
    /// и сколько строк таблицы за этой строкой ответа стоит.
    /// </summary>
    private static string Sql(RefColumn c, int asked)
    {
        var table = $"{Id(c.Schema)}.{Id(c.Table)}";
        var column = $"x.{Id(c.Column)}";
        return c.Kind switch
        {
            Kind.Uuid => $"""
                SELECT ARRAY[{column}], count(*)::int FROM {table} x
                WHERE {column} = ANY(@ids) GROUP BY {column}
                """,
            Kind.UuidArray => $"""
                SELECT ARRAY(SELECT DISTINCT u FROM unnest({column}) u WHERE u = ANY(@ids)), 1
                FROM {table} x WHERE {column} && @ids
                """,
            // Идентификаторы строки вырезаются выражением и сравниваются с массивом — но только у строк,
            // прошедших дешёвый отбор: подстрокой, когда спрошено немного, иначе «есть ли идентификатор
            // вообще».
            _ => $"""
                SELECT ARRAY(SELECT DISTINCT m[1]::uuid
                             FROM regexp_matches(lower({column}::text), @uuid, 'g') m
                             WHERE m[1]::uuid = ANY(@ids)), 1
                FROM {table} x
                WHERE {(asked <= LikeUpTo ? $"lower({column}::text) LIKE ANY(@likes)" : $"lower({column}::text) ~ @uuid")}
                """,
        };
    }

    private static string Id(string name) => '"' + name.Replace("\"", "\"\"") + '"';
}
