using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace BHS.CRG.Infrastructure.Persistence;

/// <summary>Колонка вне ядра, в которой нашлись спрошенные идентификаторы.</summary>
/// <param name="Hits">Идентификатор → сколько раз встречается в колонке.</param>
public sealed record HeldColumn(string Schema, string Table, string Column, IReadOnlyDictionary<Guid, int> Hits)
{
    public string Address => $"{Schema}.{Table}.{Column}";
    public int Total => Hits.Values.Sum();
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

        // Чужую транзакцию не трогаем: снимок в ней задаёт тот, кто её открыл.
        await using var snapshot = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct)
            : null;
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();

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
            try
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = Sql(column);
                cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = asked });
                if (column.Kind == Kind.Json)
                    cmd.Parameters.Add(new NpgsqlParameter("uuid", UuidInText));

                var hits = new Dictionary<Guid, int>();
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    hits[reader.GetGuid(0)] = reader.GetInt32(1);

                if (hits.Count > 0)
                    found.Add(new HeldColumn(column.Schema, column.Table, column.Column, hits));
            }
            catch (PostgresException ex)
            {
                // Пропустить колонку нельзя: «не смог прочитать» превратилось бы в «никто не держит».
                throw new ModuleDataUnreadableException(column.Address, ex);
            }
        }

        return found;
    }

    /// <summary>
    /// Названия документов-держателей: «№ 12», «№ 15» — для колонки-идентификатора, у которой модуль
    /// объявил, чем назвать документ. Не больше <paramref name="limit" />: отказ — не отчёт.
    ///
    /// <para>Не удалось (схема отстала от объявления, колонка оказалась не идентификатором) — пустой
    /// список, а не отказ: число держателей уже посчитано сканом, названия — пояснение к нему.</para>
    /// </summary>
    public async Task<IReadOnlyList<string>> LabelsAsync(
        HeldColumn held, string documentTable, string documentKey, string labelColumn, string via,
        int limit, CancellationToken ct = default)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = connection.CreateCommand();
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

            var labels = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) labels.Add(reader.GetString(0));
            return labels;
        }
        catch (PostgresException)
        {
            return [];
        }
        finally
        {
            if (opened) await db.Database.CloseConnectionAsync();
        }
    }

    private static string Sql(RefColumn c)
    {
        var table = $"{Id(c.Schema)}.{Id(c.Table)}";
        var column = $"x.{Id(c.Column)}";
        return c.Kind switch
        {
            Kind.Uuid => $"""
                SELECT {column}, count(*)::int FROM {table} x
                WHERE {column} = ANY(@ids) GROUP BY {column}
                """,
            Kind.UuidArray => $"""
                SELECT u, count(*)::int FROM {table} x, LATERAL unnest({column}) u
                WHERE {column} && @ids AND u = ANY(@ids) GROUP BY u
                """,
            // Все идентификаторы колонки вырезаются одним проходом и сравниваются с массивом: поиск
            // подстроки на каждый идентификатор для каскада уровня был бы запросом на запись.
            _ => $"""
                SELECT m.id, count(*)::int
                FROM {table} x,
                     LATERAL (SELECT DISTINCT (regexp_matches(lower({column}::text), @uuid, 'g'))[1]::uuid AS id) m
                WHERE m.id = ANY(@ids) GROUP BY m.id
                """,
        };
    }

    private static string Id(string name) => '"' + name.Replace("\"", "\"\"") + '"';
}
