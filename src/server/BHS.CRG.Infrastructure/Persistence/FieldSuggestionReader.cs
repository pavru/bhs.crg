using System.Globalization;
using BHS.CRG.Application.Objects.FieldSuggestions;
using BHS.CRG.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace BHS.CRG.Infrastructure.Persistence;

/// <summary>
/// Чтение окна подсказок сырым SQL (issue #1530, D2). Форма запроса — часть решения, и ответ её не
/// выдаёт: неверная форма отдаёт тот же список, только в тридцать раз дольше. Поэтому её стережёт
/// тест ПЛАНА (<c>FieldSuggestionPlanTests</c>), а не тест ответа.
///
/// <para><b>Окно отбирается без данных записей.</b> По каждому типу семьи — последние действующие
/// записи частичным индексом <c>(CompositeTypeId, UpdatedAt DESC) WHERE ArchivedAt IS NULL</c>, их
/// размер — <c>pg_column_size</c>: он виден по заголовку значения, без распаковки. Нарастающая сумма
/// размеров режет окно по объёму.</para>
///
/// <para><b>Поле читается только у записей окна</b> — подзапросом в списке выборки, который база
/// исполняет на каждую прошедшую строку по первичному ключу. Соединение здесь не годится: планировщик
/// волен выполнить его хешем с последовательным просмотром всей таблицы, и фильтр по полю снаружи окна
/// так и исполнился (264 мс, замер #1528).</para>
/// </summary>
public sealed class FieldSuggestionReader(AppDbContext db) : IFieldSuggestionReader
{
    /// <summary>Код PostgreSQL «запрос отменён по statement_timeout».</summary>
    private const string QueryCanceled = "57014";

    /// <summary>
    /// Ветка окна одного типа: последние действующие записи и их размер — без чтения данных.
    ///
    /// <para>⚠️ Тип — параметром КАЖДОЙ ветке, а не <c>unnest</c> списка с <c>LATERAL</c>. Так было
    /// первым вариантом, и тест плана его отверг: тип из <c>unnest</c> планировщику не виден, число
    /// записей он оценивает по среднему на тип и на большом типе выбирал сортировку всех его записей
    /// вместо выборки по индексу — в зависимости от того, какие ещё типы лежат в таблице.</para>
    /// </summary>
    private const string Branch = """
        (SELECT d."Id", d."ScopeLevel", d."ScopeId", d."UpdatedAt", pg_column_size(d."Data") AS "Size"
         FROM domain_objects d
         WHERE d."CompositeTypeId" = @t{0} AND d."ArchivedAt" IS NULL
         ORDER BY d."UpdatedAt" DESC
         LIMIT @rows)
        """;

    /// <summary>
    /// Запрос окна для <paramref name="types" /> типов семьи. Параметры: <c>@t0…</c> — типы, <c>@field</c>
    /// — ключ поля, <c>@rows</c> и <c>@bytes</c> — пределы окна. Открыт для теста плана: он объясняет
    /// ровно этот текст.
    /// </summary>
    public static string Sql(int types) => $"""
        SELECT w."Id", w."ScopeLevel", w."ScopeId", w."UpdatedAt",
               (SELECT CASE WHEN jsonb_typeof(o."Data" -> @field) = 'string' THEN o."Data" ->> @field END
                FROM domain_objects o WHERE o."Id" = w."Id") AS "Value"
        FROM (
            SELECT r.*, sum(r."Size") OVER (ORDER BY r."UpdatedAt" DESC, r."Id") AS "Total"
            FROM (
                SELECT l.* FROM (
                    {string.Join("\nUNION ALL\n", Enumerable.Range(0, types).Select(i => string.Format(CultureInfo.InvariantCulture, Branch, i)))}
                ) l
                ORDER BY l."UpdatedAt" DESC, l."Id"
                LIMIT @rows
            ) r
        ) w
        WHERE w."Total" <= @bytes
        """;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SuggestionRow>> ReadAsync(
        IReadOnlyCollection<Guid> typeIds, string field, SuggestionWindow window, CancellationToken ct = default)
    {
        if (typeIds.Count == 0) return [];

        // Предел времени ставится на СВОЮ транзакцию: SET LOCAL в чужой пережил бы этот запрос и
        // ограничил бы чужие. В чужой пределы задаёт её хозяин (приём ModuleReferenceScan).
        await using var own = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        try
        {
            if (own is not null)
                await using (var limit = connection.CreateCommand())
                {
                    var ms = ((int)window.Timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
                    limit.CommandText = $"SET LOCAL statement_timeout = '{ms}ms'";
                    await limit.ExecuteNonQueryAsync(ct);
                }

            var rows = new List<SuggestionRow>();
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = Sql(typeIds.Count);
                AddParameters(cmd, typeIds, field, window);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    if (reader.IsDBNull(4)) continue;
                    rows.Add(new SuggestionRow(
                        reader.GetGuid(0),
                        reader.GetString(4),
                        Enum.Parse<CatalogScope>(reader.GetString(1)),
                        reader.IsDBNull(2) ? null : reader.GetGuid(2),
                        reader.GetFieldValue<DateTimeOffset>(3)));
                }
            }

            if (own is not null) await own.CommitAsync(ct);
            return rows;
        }
        catch (PostgresException ex) when (ex.SqlState == QueryCanceled)
        {
            throw new SuggestionsTimedOutException(ex);
        }
    }

    /// <summary>Параметры <see cref="Sql" /> — общие у чтения и у теста плана.</summary>
    public static void AddParameters(
        NpgsqlCommand cmd, IReadOnlyCollection<Guid> typeIds, string field, SuggestionWindow window)
    {
        foreach (var (type, i) in typeIds.Select((t, i) => (t, i)))
            cmd.Parameters.Add(new NpgsqlParameter($"t{i}", NpgsqlDbType.Uuid) { Value = type });
        cmd.Parameters.Add(new NpgsqlParameter("field", NpgsqlDbType.Text) { Value = field });
        cmd.Parameters.Add(new NpgsqlParameter("rows", NpgsqlDbType.Integer) { Value = window.MaxRecords });
        cmd.Parameters.Add(new NpgsqlParameter("bytes", NpgsqlDbType.Bigint) { Value = window.MaxBytes });
    }
}
