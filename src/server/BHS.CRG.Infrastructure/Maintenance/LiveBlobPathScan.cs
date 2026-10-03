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
/// <para>Текстовые колонки ЯДРА берём по имени, а не все подряд: иначе под выражение пришлось бы
/// прогнать содержимое шаблонов и кэшей наборов — мегабайты ради пяти колонок.</para>
///
/// <para><b>⚠️ Схемы модулей — тоже держатели</b> (issue #1094). Скан счёта лежит в
/// <c>costs.invoices.scan_blob_path</c>, и первая редакция его не видела дважды: смотрела только
/// схему <c>public</c> и узнавала текстовую колонку по <c>BlobPath</c> в имени, а у модуля имена —
/// snake_case. Уборка считала сканы счетов ничьими, и настоящий прогон удалил бы их безвозвратно
/// (найдено ревизией Архитектора, подтверждено сухим прогоном на дев-стенде).</para>
///
/// <para>У схем вне ядра берутся ВСЕ текстовые и JSONB-колонки, без соглашения об именах: о том,
/// как модуль назовёт колонку, ядро не знает и знать не должно, а таблицы модуля малы. Объявлением
/// модуля («вот мои колонки с файлами») этот вопрос решать нельзя по той же причине, по которой
/// нельзя списком: объявление — тот же список, выписанный руками, только в другом месте, и
/// разойдётся он в ту же сторону. К тому же схема ВЫКЛЮЧЕННОГО модуля отстаёт от его кода (его
/// миграции не применяются), и объявление, написанное для нынешних имён колонок, прежних не нашло
/// бы — а данные и файлы выключенного модуля обязаны его пережить (ТЗ AUTH-19).</para>
/// </summary>
public class LiveBlobPathScan(AppDbContext db)
{
    /// <summary>Схема ядра. Всё, что лежит вне её, — данные модулей.</summary>
    private const string CoreSchema = "public";

    /// <summary>Колонка, в которой может лежать путь.</summary>
    private readonly record struct PathColumn(string Schema, string Table, string Column, bool IsJsonb);

    /// <summary>
    /// Реестр из отбора исключён: он перечисляет то, что создано, а не то, на что ссылаются.
    /// Не исключи мы его — живым оказался бы каждый путь, и уборка не нашла бы ничего никогда.
    /// </summary>
    /// <remarks>
    /// Схемы не перечисляются, а берутся все, кроме служебных: в этой базе живут только ядро и
    /// модули, а список схем модулей, выписанный здесь, пропустил бы первую же новую.
    /// </remarks>
    private const string ColumnsSql = """
        SELECT c.table_schema, c.table_name, c.column_name, (c.data_type = 'jsonb') AS is_jsonb
        FROM information_schema.columns c
        JOIN information_schema.tables t
          ON t.table_schema = c.table_schema AND t.table_name = c.table_name
        WHERE c.table_schema NOT IN ('pg_catalog', 'information_schema')
          AND c.table_schema NOT LIKE 'pg\_%'
          AND t.table_type = 'BASE TABLE'
          AND NOT (c.table_schema = 'public' AND c.table_name = 'blob_registry')
          AND (c.data_type = 'jsonb'
            OR (c.data_type IN ('text', 'character varying')
                AND (c.table_schema <> 'public' OR c.column_name ILIKE '%BlobPath%')))
        """;

    /// <summary>Все живые пути — одним множеством.</summary>
    public async Task<HashSet<string>> RunAsync(CancellationToken ct = default) =>
        (await ScanAsync(ct)).All;

    /// <summary>
    /// Живые пути и отдельно те, что держат данные модулей: отчёт уборки обязан сказать, ПОЧЕМУ файл
    /// не предложен к удалению, а «используется» без адреса читается как «кем-то, не знаю кем».
    /// </summary>
    public async Task<LiveBlobPaths> ScanAsync(CancellationToken ct = default)
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
                    columns.Add(new PathColumn(
                        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
            }

            var paths = new HashSet<string>(StringComparer.Ordinal);
            var inModules = new HashSet<string>(StringComparer.Ordinal);
            foreach (var column in columns)
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = column.IsJsonb ? JsonbSql(column) : TextSql(column);
                cmd.CommandTimeout = 600;
                cmd.Parameters.Add(new NpgsqlParameter("shape", BlobPathShape.Pattern));
                if (column.IsJsonb) cmd.Parameters.Add(new NpgsqlParameter("rough", BlobPathShape.RoughPattern));

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    if (reader.IsDBNull(0)) continue;
                    var path = reader.GetString(0);
                    paths.Add(path);
                    if (column.Schema != CoreSchema) inModules.Add(path);
                }
            }

            return new LiveBlobPaths(paths, inModules);
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
        FROM {{Id(c.Schema)}}.{{Id(c.Table)}} x, LATERAL jsonb_path_query(x.{{Id(c.Column)}}, '$.**') v
        WHERE x.{{Id(c.Column)}}::text ~ @rough
          AND jsonb_typeof(v) = 'string'
          AND (v #>> '{}') ~ @shape
        """;

    private static string TextSql(PathColumn c) => $"""
        SELECT DISTINCT x.{Id(c.Column)} FROM {Id(c.Schema)}.{Id(c.Table)} x WHERE x.{Id(c.Column)} ~ @shape
        """;

    /// <summary>
    /// Идентификатор в кавычках: имена таблиц в схеме snake_case, а колонок — PascalCase, и без
    /// кавычек Postgres сложил бы <c>BlobPath</c> в <c>blobpath</c>. Кавычку внутри имени удваиваем —
    /// имена приходят из <c>information_schema</c>, но подстановка в SQL без экранирования была бы
    /// заготовкой для инъекции при первой же смене источника списка.
    /// </summary>
    private static string Id(string name) => '"' + name.Replace("\"", "\"\"") + '"';
}

/// <summary>Что нашёл скан держателей.</summary>
/// <param name="All">Все пути, на которые ссылается база.</param>
/// <param name="InModules">Из них те, что найдены в схемах модулей (включённых и выключенных).</param>
public sealed record LiveBlobPaths(HashSet<string> All, HashSet<string> InModules);
