using BHS.CRG.Application.Objects;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BHS.CRG.Infrastructure.Persistence;

/// <inheritdoc cref="IReferenceIndex" />
public class ReferenceIndex(AppDbContext db) : IReferenceIndex
{
    public Task<IReadOnlyList<Guid>> ObjectsMentioningAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
        => QueryAsync(Sql("domain_objects", "Data"), ids, ct);

    public Task<IReadOnlyList<Guid>> QualityDocumentsMentioningAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
        => QueryAsync(Sql("quality_documents", "Requisites"), ids, ct);

    /// <summary>
    /// Строки, в JSONB которых есть строковый узел, равный одному из идентификаторов.
    ///
    /// <para><c>jsonb_path_query('$.**')</c> обходит РАЗОБРАННЫЙ jsonb, а не текст, — ссылка лежит
    /// на произвольной глубине (внутри массивов строк таблицы, внутри составных значений), и по
    /// верхнему уровню её не найти. Грубого предварительного отбора по <c>::text</c>, как в
    /// сканере блобов, здесь сознательно нет: там искали строку неизвестной формы во ВСЕХ JSONB
    /// схемы, и отбор экономил разбор чужих таблиц; здесь колонок две и известных, а
    /// материализация той же самой строки в текст стоила бы ровно того, что призвана сэкономить.</para>
    ///
    /// <para><b>Сравнение по нормализованной форме.</b> <c>Guid.TryParse</c> в C# принимает и
    /// <c>D</c> (с дефисами), и <c>N</c>, и формы в скобках, и регистр ему безразличен — а прямое
    /// сравнение строк не приняло бы ничего, кроме точного совпадения. Сузь мы шире, чем понимает
    /// C#, — держатель, чей идентификатор записан в другой форме, не попал бы в отбор, и guard
    /// пропустил бы настоящую ссылку. Поэтому с обеих сторон снимаются дефисы и скобки и
    /// приводится регистр: у идентификаторов это обратимо, а посторонняя строка от такой
    /// нормализации совпасть с 32 шестнадцатеричными знаками не может — и даже совпади, лишний
    /// кандидат стоит одного разбора JSON.</para>
    /// </summary>
    private static string Sql(string table, string column) =>
        "SELECT x.\"Id\" FROM " + table + " x WHERE EXISTS ("
        + " SELECT 1 FROM jsonb_path_query(x.\"" + column + "\", '$.**') v"
        + " WHERE jsonb_typeof(v) = 'string'"
        + "   AND lower(translate(v #>> '{}', '{}()-', '')) = ANY(@ids))";

    private async Task<IReadOnlyList<Guid>> QueryAsync(
        string sql, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere) await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            // «N» — те же 32 знака без дефисов и в нижнем регистре, что даёт translate+lower в SQL.
            cmd.Parameters.Add(new NpgsqlParameter("ids", ids.Select(i => i.ToString("N")).ToArray()));

            var found = new List<Guid>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) found.Add(reader.GetGuid(0));
            return found;
        }
        finally
        {
            if (openedHere) await db.Database.CloseConnectionAsync();
        }
    }
}
