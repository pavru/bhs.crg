using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace BHS.CRG.Infrastructure.Persistence;

/// <summary>Таблица ядра, в которой живут записи одного вида.</summary>
public sealed record CoreTable(string Schema, string Table, string Key);

/// <summary>Колонка вне ядра, о которой спрашивают: целы ли её ссылки.</summary>
/// <param name="Target">Где искать цель; <c>null</c> — вид цели не назван, проверять негде.</param>
/// <param name="Via">Колонка той же таблицы с ключом документа; <c>null</c> — не названа.</param>
public sealed record ReferencingColumn(string Table, string Column, CoreTable? Target, string? Via);

/// <summary>Ссылка, цели которой в ядре нет.</summary>
/// <param name="Rows">Сколько строк таблицы несут её в этом документе.</param>
public sealed record LostHit(ReferencingColumn Column, Guid TargetId, Guid? DocumentKey, int Rows);

public enum UnscannedReason { NoTarget, Unreadable, MissingInSchema }

public sealed record UnscannedColumn(ReferencingColumn Column, UnscannedReason Reason);

public sealed record LostScan(
    IReadOnlyList<LostHit> Lost, IReadOnlyList<UnscannedColumn> Unscanned, DateTimeOffset AsOf);

/// <summary>
/// Обратный опрос: в каких колонках вне ядра стоят идентификаторы записей, которых в ядре нет (ТЗ
/// CORE-34.2, issue #1184).
///
/// <para>Зеркало <see cref="ModuleReferenceScan" />: тот ищет держателей записи перед удалением, этот —
/// ссылки, оставшиеся после удаления в обход отказа (восстановление копии, гонка, правка базы руками).
/// О модулях не знает так же: колонки и таблицу цели ему называет корень композиции.</para>
///
/// <para><b>Колонки — по объявлениям, а не сканом схемы,</b> и это не противоречит решению о прямом
/// скане. Там забытая колонка означала бы потерю данных, и довериться списку было нельзя. Здесь
/// найти цель можно только зная её ВИД, а вид известен лишь из объявления. Колонку, которой в схеме
/// не оказалось, опрос называет непроверенной — молча пропустить её значило бы ответить «потерь нет».</para>
///
/// <para>Анти-соединение с таблицей цели, а не «собрать идентификаторы и спросить»: один проход
/// колонки, и между «собрал» и «спросил» ничего не успевает измениться. Всё в одном снимке.</para>
/// </summary>
public class ModuleLostReferenceScan(AppDbContext db)
{
    private const string LockTimeout = "5s";
    private const string StatementTimeout = "60s";
    private const string Savepoint = "lost_reference_scan";

    /// <summary>Таблица ядра по типу сущности — из модели, а не строкой: переименование таблицы
    /// миграцией не оставит опрос смотреть в пустоту.</summary>
    public CoreTable TableOf(Type entity)
    {
        var type = db.Model.FindEntityType(entity)
            ?? throw new InvalidOperationException($"В модели ядра нет сущности {entity.Name}.");
        var table = type.GetTableName()
            ?? throw new InvalidOperationException($"У сущности {entity.Name} нет таблицы.");
        var key = type.FindPrimaryKey()?.Properties is [var single]
            ? single.GetColumnName()
            : throw new InvalidOperationException($"У сущности {entity.Name} ключ не из одной колонки.");
        return new CoreTable(type.GetSchema() ?? "public", table, key);
    }

    /// <summary>
    /// Какие из объектов общей таблицы есть — и лежит ли каждый в архиве (issue #1185). Ключа нет —
    /// объекта нет. Отдельным методом, а не колонкой в <see cref="ExistingAsync" />: тот спрашивает
    /// любую таблицу ядра по её ключу, а архив есть только здесь. Одним запросом, а не двумя: ответ
    /// нужен на каждое открытие счёта.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, bool>> RecordStatesAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return new Dictionary<Guid, bool>();
        var asked = ids.Distinct().ToArray();
        return await db.DomainObjects.AsNoTracking()
            .Where(o => asked.Contains(o.Id))
            .Select(o => new { o.Id, Archived = o.ArchivedAt != null })
            .ToDictionaryAsync(o => o.Id, o => o.Archived, ct);
    }

    /// <summary>Какие из идентификаторов есть в таблице ядра.</summary>
    public async Task<IReadOnlySet<Guid>> ExistingAsync(
        CoreTable target, IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        var found = new HashSet<Guid>();
        if (ids.Count == 0) return found;

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction();
            cmd.CommandText = $"SELECT t.{Id(target.Key)} FROM {Id(target.Schema)}.{Id(target.Table)} t WHERE t.{Id(target.Key)} = ANY(@ids)";
            cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids.Distinct().ToArray() });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) found.Add(reader.GetGuid(0));
            return found;
        }
        finally
        {
            if (opened) await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Потерянные ссылки в названных колонках схемы — одним снимком базы.</summary>
    public async Task<LostScan> FindAsync(
        string schema, IReadOnlyList<ReferencingColumn> columns, CancellationToken ct = default)
    {
        // Чужую транзакцию не трогаем: снимок и пределы в ней задаёт тот, кто её открыл.
        var foreign = db.Database.CurrentTransaction;
        await using var own = foreign is null
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct)
            : null;
        var transaction = foreign ?? own!;
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();

        if (foreign is null)
            await ExecuteAsync(connection, $"SET LOCAL lock_timeout = '{LockTimeout}'; SET LOCAL statement_timeout = '{StatementTimeout}'", ct);

        var asOf = await SnapshotTimeAsync(connection, ct);
        var uuids = await UuidColumnsAsync(connection, schema, ct);

        var lost = new List<LostHit>();
        var unscanned = new List<UnscannedColumn>();
        foreach (var column in columns)
        {
            if (column.Target is not { } target)
            {
                unscanned.Add(new(column, UnscannedReason.NoTarget));
                continue;
            }
            // Колонки нет или она не идентификатор — схема отстала от объявления (модуль выключен и
            // не домигрирован). «Нет столбца» не значит «ссылок нет».
            if (!uuids.Contains((column.Table, column.Column)))
            {
                unscanned.Add(new(column, UnscannedReason.MissingInSchema));
                continue;
            }
            var via = column.Via is { } key && uuids.Contains((column.Table, key)) ? $"x.{Id(key)}" : "NULL::uuid";

            // Отказ базы прерывает транзакцию целиком; точка сохранения оставляет остальные колонки
            // проверяемыми, а эту — названной непроверенной.
            await transaction.CreateSavepointAsync(Savepoint, ct);
            try
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = $"""
                    SELECT x.{Id(column.Column)}, {via}, count(*)::int
                    FROM {Id(schema)}.{Id(column.Table)} x
                    WHERE x.{Id(column.Column)} IS NOT NULL
                      AND NOT EXISTS (SELECT 1 FROM {Id(target.Schema)}.{Id(target.Table)} t
                                      WHERE t.{Id(target.Key)} = x.{Id(column.Column)})
                    GROUP BY 1, 2
                    """;
                await using (var reader = await cmd.ExecuteReaderAsync(ct))
                    while (await reader.ReadAsync(ct))
                        lost.Add(new(column, reader.GetGuid(0),
                            await reader.IsDBNullAsync(1, ct) ? null : reader.GetGuid(1), reader.GetInt32(2)));
                await transaction.ReleaseSavepointAsync(Savepoint, ct);
            }
            catch (PostgresException)
            {
                await transaction.RollbackToSavepointAsync(Savepoint, ct);
                lost.RemoveAll(hit => ReferenceEquals(hit.Column, column));
                unscanned.Add(new(column, UnscannedReason.Unreadable));
            }
        }

        if (own is not null) await own.CommitAsync(ct);
        return new LostScan(lost, unscanned, asOf);
    }

    private static async Task<DateTimeOffset> SnapshotTimeAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT transaction_timestamp()";
        return new DateTimeOffset((DateTime)(await cmd.ExecuteScalarAsync(ct))!, TimeSpan.Zero);
    }

    /// <summary>Колонки-идентификаторы схемы — из каталога базы, по той же причине, что у прямого
    /// скана: <c>information_schema</c> показывает только то, на что у роли есть право.</summary>
    private static async Task<HashSet<(string Table, string Column)>> UuidColumnsAsync(
        NpgsqlConnection connection, string schema, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT c.relname, a.attname
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_type t ON t.oid = a.atttypid
            WHERE a.attnum > 0 AND NOT a.attisdropped AND c.relkind IN ('r', 'p')
              AND n.nspname = @schema AND t.typname = 'uuid'
            """;
        cmd.Parameters.Add(new NpgsqlParameter("schema", schema));

        var found = new HashSet<(string, string)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) found.Add((reader.GetString(0), reader.GetString(1)));
        return found;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Id(string name) => '"' + name.Replace("\"", "\"\"") + '"';
}
