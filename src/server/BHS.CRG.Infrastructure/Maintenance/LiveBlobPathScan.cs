using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BHS.CRG.Infrastructure.Maintenance;

/// <summary>
/// Все пути блобов, на которые СЕЙЧАС ссылается база (issue #741).
///
/// <para>Отвечает на единственный вопрос сборщика мусора: «на этот объект ещё кто-нибудь
/// показывает?» Ответ обязан быть полным — неучтённый держатель означает, что уборка удалит
/// работающий файл, и это худший исход из возможных.</para>
///
/// <para><b>Почему схема, а не список держателей.</b> В issue список был выписан руками
/// (<c>generated_files</c>, <c>document_set_outputs</c>, скан документа качества, ассеты шаблонов,
/// файлы наборов, вложения в реквизитах), и именно так делать нельзя. Путь обычного вложения не
/// лежит ни в одной колонке — он внутри JSONB реквизитов, куда его кладёт клиент; JSONB-колонок в
/// схеме больше тридцати. Список, выписанный руками, разойдётся с моделью при первом же новом поле
/// — молча, ничего не сломав на вид, и разойдётся в сторону удаления живого файла. Поэтому
/// держатели берутся из <c>information_schema</c>: все JSONB-колонки плюс текстовые, у которых в
/// имени есть <c>BlobPath</c>.</para>
///
/// <para>Тот же способ выбран разовым сбором реестра (<see cref="BlobRegistryBackfill" />), и это
/// не совпадение: там ищут «что уже создано», здесь — «что ещё нужно», а множество мест одно.
/// Расхождение двух списков означало бы, что сборщик считает сиротой то, что сбор считает живым, —
/// на это есть тест (<c>OrphanBlobCleanupTests</c>).</para>
///
/// <para>Текстовые колонки берём по имени, а не все подряд: иначе под выражение пришлось бы прогнать
/// содержимое шаблонов и кэшей наборов — мегабайты ради пяти колонок.</para>
/// </summary>
public class LiveBlobPathScan(AppDbContext db)
{
    /// <summary>Колонка, в которой может лежать путь.</summary>
    private readonly record struct PathColumn(string Table, string Column, bool IsJsonb);

    /// <summary>
    /// Реестр из отбора исключён: он перечисляет то, что создано, а не то, на что ссылаются.
    /// Не исключи мы его — живым оказался бы каждый путь, и уборка не нашла бы ничего никогда.
    /// </summary>
    private const string ColumnsSql = """
        SELECT c.table_name, c.column_name, (c.data_type = 'jsonb') AS is_jsonb
        FROM information_schema.columns c
        JOIN information_schema.tables t
          ON t.table_schema = c.table_schema AND t.table_name = c.table_name
        WHERE c.table_schema = 'public'
          AND t.table_type = 'BASE TABLE'
          AND c.table_name <> 'blob_registry'
          AND (c.data_type = 'jsonb'
            OR (c.data_type IN ('text', 'character varying') AND c.column_name ILIKE '%BlobPath%'))
        """;

    public async Task<HashSet<string>> RunAsync(CancellationToken ct = default)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();

        // Открыли — закрываем сами. Открытие через EF увеличивает его счётчик, и без парного
        // закрытия соединение из пула остаётся приколотым к контексту до конца запроса — а после
        // скана идёт цикл удаления, который на большой партии длится минуты.
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere) await db.Database.OpenConnectionAsync(ct);
        try
        {
            var columns = new List<PathColumn>();
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = ColumnsSql;
                cmd.CommandTimeout = 600;
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    columns.Add(new PathColumn(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
            }

            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var column in columns)
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = column.IsJsonb ? JsonbSql(column) : TextSql(column);
                cmd.CommandTimeout = 600;
                cmd.Parameters.Add(new NpgsqlParameter("shape", BlobPathShape.Pattern));
                if (column.IsJsonb) cmd.Parameters.Add(new NpgsqlParameter("rough", BlobPathShape.RoughPattern));

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    if (!reader.IsDBNull(0)) paths.Add(reader.GetString(0));
            }

            return paths;
        }
        finally
        {
            if (openedHere) await db.Database.CloseConnectionAsync();
        }
    }

    /// <remarks>
    /// Порядок отбора важен для стоимости: сначала грубый отбор СТРОК по тексту колонки
    /// (<c>~ @rough</c>), и только у прошедших разворачиваем JSON. Иначе <c>jsonb_path_query</c>
    /// проходит по каждому узлу каждого документа, а в <c>domain_objects."Data"</c> это картинки в
    /// base64 — мегабайты на запись.
    /// </remarks>
    // $$ вместо $: в запросе есть литерал '{}' оператора #>>, и при одинарном $ он был бы принят за
    // дыру интерполяции. С двойным дырой считается только {{…}}.
    private static string JsonbSql(PathColumn c) => $$"""
        SELECT DISTINCT v #>> '{}'
        FROM {{Id(c.Table)}} x, LATERAL jsonb_path_query(x.{{Id(c.Column)}}, '$.**') v
        WHERE x.{{Id(c.Column)}}::text ~ @rough
          AND jsonb_typeof(v) = 'string'
          AND (v #>> '{}') ~ @shape
        """;

    private static string TextSql(PathColumn c) => $"""
        SELECT DISTINCT x.{Id(c.Column)} FROM {Id(c.Table)} x WHERE x.{Id(c.Column)} ~ @shape
        """;

    /// <summary>
    /// Идентификатор в кавычках: имена таблиц в схеме snake_case, а колонок — PascalCase, и без
    /// кавычек Postgres сложил бы <c>BlobPath</c> в <c>blobpath</c>. Кавычку внутри имени удваиваем —
    /// имена приходят из <c>information_schema</c>, но подстановка в SQL без экранирования была бы
    /// заготовкой для инъекции при первой же смене источника списка.
    /// </summary>
    private static string Id(string name) => '"' + name.Replace("\"", "\"\"") + '"';
}
