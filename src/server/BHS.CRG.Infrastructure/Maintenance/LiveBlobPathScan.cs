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
/// <para>Тот же способ выбран разовым сбором реестра (<see cref="BlobRegistryBackfill" />): там
/// искали «что уже создано», здесь — «что ещё нужно». В схеме ЯДРА множество мест у них одно, и
/// расхождение означало бы, что сборщик считает сиротой то, что сбор считает живым, — на это есть
/// тест (<c>OrphanBlobCleanupTests</c>). Схем модулей сбор не смотрит и не должен: он прошёл один
/// раз, миграцией ядра, до появления первого модуля, а файлы модулей попадают в реестр при записи.</para>
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
/// <para>О данных модуля ядро не знает ничего — ни имён колонок, ни их типов, ни того, в каком виде
/// модуль хранит путь. Поэтому вне ядра читается КАЖДАЯ колонка, кроме заведомо не текстовых (числа,
/// даты, идентификаторы), — приведённая к тексту, — и ищется в ней не путь целиком, а его ключ
/// (<see cref="BlobPathShape.KeyInText" />): так находится путь в массиве, в JSON, записанном
/// строкой, после адреса сервера. Список типов — запретительный нарочно: неизвестный тип читается,
/// а не пропускается (ревью PR #1183: разрешительный список из трёх типов не видел массивов).
/// Лишнее совпадение стоит одного неубранного файла, пропущенное — одного удалённого.</para>
///
/// <para>Объявлением модуля («вот мои колонки с файлами») этот вопрос решать нельзя по той же
/// причине, по которой нельзя списком: объявление — тот же список, выписанный руками, только в
/// другом месте, и разойдётся он в ту же сторону. К тому же схема ВЫКЛЮЧЕННОГО модуля отстаёт от его
/// кода (его миграции не применяются), и объявление, написанное для нынешних имён колонок, прежних
/// не нашло бы — а данные и файлы выключенного модуля обязаны его пережить (ТЗ AUTH-19).</para>
///
/// <para><b>Цена.</b> Таблицы модуля читаются целиком, без грубого предварительного отбора. Для
/// действия, которое администратор запускает руками, это приемлемо; станет заметно — мерить, а не
/// сужать отбор по догадке.</para>
/// </summary>
public class LiveBlobPathScan(AppDbContext db)
{
    /// <summary>Как читать колонку: строки JSONB ядра, текст ядра целиком, что угодно вне ядра.</summary>
    private enum Kind { CoreJsonb, CoreText, Module }

    /// <summary>Колонка, в которой может лежать путь.</summary>
    private readonly record struct PathColumn(string Schema, string Table, string Column, Kind Kind)
    {
        public string Address => $"{Schema}.{Table}.{Column}";
    }

    /// <summary>
    /// Реестр из отбора исключён: он перечисляет то, что создано, а не то, на что ссылаются.
    /// Не исключи мы его — живым оказался бы каждый путь, и уборка не нашла бы ничего никогда.
    /// </summary>
    /// <remarks>
    /// Схемы не перечисляются, а берутся все, кроме служебных: список схем модулей, выписанный здесь,
    /// пропустил бы первую же новую, а схему модуля, вынутого из сборки, не назвал бы никто. Если в
    /// базе живёт что-то постороннее, его колонки тоже читаются — и колонка, которую прочитать
    /// нельзя, останавливает уборку (см. <see cref="BlobScanRefusedException" />).
    /// </remarks>
    private const string ColumnsSql = """
        SELECT c.table_schema, c.table_name, c.column_name,
               CASE WHEN c.table_schema <> 'public' THEN 2
                    WHEN c.data_type = 'jsonb' THEN 0
                    ELSE 1 END AS kind
        FROM information_schema.columns c
        JOIN information_schema.tables t
          ON t.table_schema = c.table_schema AND t.table_name = c.table_name
        WHERE c.table_schema NOT IN ('pg_catalog', 'information_schema')
          AND c.table_schema NOT LIKE 'pg\_%'
          AND t.table_type = 'BASE TABLE'
          AND NOT (c.table_schema = 'public' AND c.table_name = 'blob_registry')
          AND CASE WHEN c.table_schema = 'public'
                   THEN c.data_type = 'jsonb'
                     OR (c.data_type IN ('text', 'character varying') AND c.column_name ILIKE '%BlobPath%')
                   ELSE c.data_type NOT IN (
                     'uuid', 'boolean', 'smallint', 'integer', 'bigint', 'numeric', 'real',
                     'double precision', 'date', 'interval', 'bytea',
                     'timestamp with time zone', 'timestamp without time zone',
                     'time with time zone', 'time without time zone')
              END
        ORDER BY c.table_schema, c.table_name, c.ordinal_position
        """;

    /// <summary>
    /// Кто что держит. Список колонок и чтение каждой идут в ОДНОМ снимке базы.
    ///
    /// <para>Снимок — не аккуратность (ревью PR #1183). Колонок десятки, читаются они по очереди, и
    /// без общего снимка ссылка, переехавшая за это время из ещё не прочитанной колонки в уже
    /// прочитанную, не нашлась бы ни там, ни там: файл с живой ссылкой ушёл бы в кандидаты. То, что
    /// появилось ПОСЛЕ снимка, скан не видит вовсе, и защищает такие файлы возрастной порог уборки.</para>
    /// </summary>
    /// <exception cref="BlobScanRefusedException">Колонку не удалось прочитать.</exception>
    public async Task<LiveBlobPaths> ScanAsync(CancellationToken ct = default)
    {
        // Чужую транзакцию не трогаем: снимок в ней задаёт тот, кто её открыл. Своя закрывается
        // вместе со сканом — после него идёт цикл удаления, который на большой партии длится минуты,
        // и держать под него соединение со снимком незачем.
        await using var snapshot = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct)
            : null;
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();

        var columns = new List<PathColumn>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = ColumnsSql;
            cmd.CommandTimeout = 600;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                columns.Add(new PathColumn(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), (Kind)reader.GetInt32(3)));
        }

        var core = new HashSet<string>(StringComparer.Ordinal);
        var moduleKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            try
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandTimeout = 600;
                switch (column.Kind)
                {
                    case Kind.CoreJsonb:
                        cmd.CommandText = JsonbSql(column);
                        cmd.Parameters.Add(new NpgsqlParameter("shape", BlobPathShape.Pattern));
                        cmd.Parameters.Add(new NpgsqlParameter("rough", BlobPathShape.RoughPattern));
                        break;
                    case Kind.CoreText:
                        cmd.CommandText = TextSql(column);
                        cmd.Parameters.Add(new NpgsqlParameter("shape", BlobPathShape.Pattern));
                        break;
                    default:
                        cmd.CommandText = AnyTextSql(column);
                        cmd.Parameters.Add(new NpgsqlParameter("key", BlobPathShape.KeyInText));
                        break;
                }

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    if (reader.IsDBNull(0)) continue;
                    var found = reader.GetString(0);
                    if (column.Kind != Kind.Module) core.Add(found);
                    else if (BlobPathShape.KeyOf(found) is { } key) moduleKeys.Add(key);
                }
            }
            catch (PostgresException ex)
            {
                // Пропустить колонку и идти дальше нельзя: «не смог прочитать» превратилось бы в
                // «держателей нет», а это и есть удаление живого файла. Отказываем, называя адрес, —
                // иначе это 500 без единого слова о том, какая таблица виновата.
                throw new BlobScanRefusedException(column.Address, ex);
            }
        }

        return new LiveBlobPaths(core, moduleKeys);
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

    /// <summary>Все ключи путей в колонке любого типа: значение приводится к тексту.</summary>
    private static string AnyTextSql(PathColumn c) => $"""
        SELECT DISTINCT (regexp_matches(x.{Id(c.Column)}::text, @key, 'g'))[1]
        FROM {Id(c.Schema)}.{Id(c.Table)} x
        WHERE x.{Id(c.Column)}::text ~ @key
        """;

    /// <summary>
    /// Идентификатор в кавычках: имена таблиц в схеме snake_case, а колонок — PascalCase, и без
    /// кавычек Postgres сложил бы <c>BlobPath</c> в <c>blobpath</c>. Кавычку внутри имени удваиваем —
    /// имена приходят из <c>information_schema</c>, но подстановка в SQL без экранирования была бы
    /// заготовкой для инъекции при первой же смене источника списка.
    /// </summary>
    private static string Id(string name) => '"' + name.Replace("\"", "\"\"") + '"';
}

/// <summary>
/// Что нашёл скан держателей. Держатели ядра известны полными путями, держатели вне ядра — ключами
/// путей (<see cref="BlobPathShape.KeyOf" />): в каком виде модуль хранит путь, ядро не знает.
/// </summary>
public sealed class LiveBlobPaths(HashSet<string> core, HashSet<string> moduleKeys)
{
    /// <summary>Пути, на которые ссылается схема ядра.</summary>
    public IReadOnlySet<string> Core => core;

    public bool HeldByCore(string path) => core.Contains(path);

    /// <summary>Файл упомянут в данных вне ядра — модуля, включённого или выключенного.</summary>
    public bool HeldByModules(string path) =>
        BlobPathShape.KeyOf(path) is { } key && moduleKeys.Contains(key);

    public bool IsLive(string path) => HeldByCore(path) || HeldByModules(path);
}

/// <summary>
/// Скан держателей не дочитан — уборка не делается (ревью PR #1183).
///
/// <para>Скан читает все схемы базы, и первая же колонка, которую прочитать нельзя (нет прав на
/// постороннюю схему, таблицу убрала идущая миграция модуля), роняла уборку ответом 500 без адреса.
/// Отказ остаётся — неполный скан опаснее несделанной уборки, — но называет колонку и отдаётся
/// человеку текстом.</para>
/// </summary>
public sealed class BlobScanRefusedException(string address, PostgresException cause)
    : InvalidOperationException(
        $"Уборка не выполнена: не удалось прочитать {address} ({cause.MessageText}). "
        + "Пока эти данные не прочитаны, нельзя сказать, на какие файлы они ссылаются, и удалять "
        + "что-либо небезопасно. Если таблица посторонняя — дайте учётной записи приложения право "
        + "чтения или вынесите таблицу из этой базы; если идёт обновление модуля — повторите позже.",
        cause)
{
    public string Address { get; } = address;
}
