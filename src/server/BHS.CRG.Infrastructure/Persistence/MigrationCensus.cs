using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BHS.CRG.Infrastructure.Persistence;

/// <summary>
/// Перепись справочника ядра ДО и ПОСЛЕ миграции (ТЗ CORE-29/CORE-31, issue #960).
///
/// <para>Зачем. На стройках и разделах стоит весь <c>CatalogScope</c>: комплекты, документы, общие
/// данные, наборы и профили уровней адресуют уровни через них. Миграция, которая потеряет стройку
/// или обрубит ссылку, не сообщит об этом ничем: приложение поднимется, а пропажа обнаружится
/// открытым комплектом без документов — днями позже и у заказчика.</para>
///
/// <para>Поэтому расхождение переписи — ОТКАЗ СТАРТА, а не запись в журнале: половинчатый старт
/// запрещён (CORE-31), и «поднялось, но данных меньше» — ровно тот случай, ради которого правило
/// записано. Журнал прочитали бы после звонка заказчика.</para>
///
/// <para>⚠️ Читается СЫРЫМ SQL, а не через EF. До миграции модель и схема не совпадают по
/// определению — это и есть причина миграции, — и запрос через контекст упал бы на первой же новой
/// колонке, то есть сторож ронял бы старт вместо того, чтобы его проверить.</para>
///
/// <para>⚠️ Считается то, что ЕСТЬ, а не «всё или ничего» (ревью PR #1046). База старше объединения
/// в <c>domain_objects</c> не знает этой таблицы — и перепись «всё или ничего» выключилась бы на
/// ней целиком и молча: самое рискованное обновление, через десятки версий, прошло бы без единой
/// проверки и выглядело бы сошедшимся. Здесь непосчитанное просто отсутствует в
/// <see cref="Counts" />, а сверка требует объяснения, если строка ПРОПАЛА между «до» и «после».</para>
/// </summary>
/// <param name="Counts">Что посчитано: подпись → число. Подпись уходит в текст отказа как есть.</param>
public sealed record MigrationCensus(IReadOnlyDictionary<string, long> Counts)
{
    private const string Constructions = "constructions";
    private const string Sections = "sections";
    private const string Sets = "document_sets";
    private const string Objects = "domain_objects";
    private const string Types = "document_types";

    /// <summary>
    /// Перепись или <c>null</c>, если справочника нет вовсе — пустая база, первый запуск. Отличать
    /// обязательно: «ноль строек» и «таблицы нет» — разные вещи, и первую миграцию сверять не с чем.
    /// </summary>
    public static async Task<MigrationCensus?> ReadAsync(DbContext db, CancellationToken ct = default)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            try
            {
                await conn.OpenAsync(ct);
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InvalidCatalogName)
            {
                // БАЗЫ ЕЩЁ НЕТ — первый запуск: её создаст сама миграция. Сверять не с чем, и это
                // не отказ. Без этой ветки сторож ронял бы ЧИСТУЮ УСТАНОВКУ: приложение падало бы
                // до первой миграции, то есть install.sh у заказчика не поднялся бы вовсе
                // (поймано CI PR #1046 — локально база уже была, и проверка прошла вхолостую).
                return null;
            }
        }

        try
        {
            var present = await PresentTablesAsync(conn, ct);
            if (!present.Contains(Constructions) && !present.Contains(Sections) && !present.Contains(Sets))
                return null;

            var parts = new List<(string Label, string Sql)>();
            if (present.Contains(Constructions)) parts.Add(("строек", "SELECT count(*) FROM constructions"));
            if (present.Contains(Sections)) parts.Add(("разделов", "SELECT count(*) FROM sections"));
            if (present.Contains(Sets)) parts.Add(("комплектов", "SELECT count(*) FROM document_sets"));

            // Сироты — ссылка есть, а того, на что она указывает, нет. Считаются отдельно от итогов,
            // потому что «стройки все на месте, но разделы отвязались» — это тоже потеря, и по одним
            // лишь счётчикам она не видна. Пара считается, только если есть ОБЕ её таблицы.
            if (present.Contains(Sections) && present.Contains(Constructions))
                parts.Add(("разделов без своей стройки",
                    "SELECT count(*) FROM sections s WHERE NOT EXISTS " +
                    "(SELECT 1 FROM constructions c WHERE c.\"Id\" = s.\"ConstructionId\")"));

            if (present.Contains(Sets) && present.Contains(Sections))
                parts.Add(("комплектов без своего раздела",
                    "SELECT count(*) FROM document_sets d WHERE NOT EXISTS " +
                    "(SELECT 1 FROM sections s WHERE s.\"Id\" = d.\"SectionId\")"));

            if (present.Contains(Objects) && present.Contains(Constructions)
                && present.Contains(Sections) && present.Contains(Sets))
                parts.Add(("объектов без своего уровня",
                    "SELECT count(*) FROM domain_objects o WHERE o.\"ScopeId\" IS NOT NULL AND (" +
                    "   (o.\"ScopeLevel\" = 'Construction' AND NOT EXISTS " +
                    "      (SELECT 1 FROM constructions c WHERE c.\"Id\" = o.\"ScopeId\"))" +
                    " OR (o.\"ScopeLevel\" = 'Section' AND NOT EXISTS " +
                    "      (SELECT 1 FROM sections s WHERE s.\"Id\" = o.\"ScopeId\"))" +
                    " OR (o.\"ScopeLevel\" = 'Set' AND NOT EXISTS " +
                    "      (SELECT 1 FROM document_sets d WHERE d.\"Id\" = o.\"ScopeId\")))"));

            // Поля справочника номенклатуры — счёт СКВОЗЬ наследование (issue #963). Миграция
            // поднимает общие поля «Материала» в новый базовый тип «Номенклатура», и ошибка здесь
            // выглядела бы так: поля из схемы ушли, родитель не встал — документы печатают пустоту
            // там, где было наименование материала. По счёту это видно сразу, и остановка старта
            // приходит раньше первого испорченного PDF.
            //
            // ⚠️ Считаются РАЗНЫЕ ключи по всей цепочке родителей, а исключения унаследованных
            // полей (excludedFields) не учитываются: формула нужна одинаковая ДО и ПОСЛЕ, а не
            // точная — сверка ищет расхождение, а не показывает схему.
            if (present.Contains(Types) && await HasColumnAsync(conn, Types, "ParentId", ct))
                parts.Add(("полей у справочника номенклатуры", EffectiveFieldsSql("Материал")));

            await using var cmd = new NpgsqlCommand(
                "SELECT " + string.Join(", ", parts.Select(p => "(" + p.Sql + ")")), conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;

            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            for (var i = 0; i < parts.Count; i++) counts[parts[i].Label] = reader.GetInt64(i);
            return new MigrationCensus(counts);
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
    }

    /// <summary>
    /// Сколько РАЗНЫХ ключей полей у типа с этим кодом вместе с унаследованными. Ноль — и когда
    /// полей нет, и когда самого типа нет: перепись сверяет число с числом, а «типа не стало»
    /// поймается как расхождение на том же счётчике.
    /// </summary>
    private static string EffectiveFieldsSql(string code) =>
        """
        WITH RECURSIVE chain AS (
            SELECT t."Id", t."ParentId", t."Schema" AS s
              FROM document_types t WHERE t."Code" = '@code@'
             UNION ALL
            SELECT p."Id", p."ParentId", p."Schema"
              FROM document_types p JOIN chain c ON p."Id" = c."ParentId")
        SELECT count(DISTINCT f->>'key') FROM chain,
             jsonb_array_elements(coalesce(chain.s->'fields', '[]'::jsonb)) f
        """.Replace("@code@", code);

    /// <summary>
    /// Есть ли у таблицы такая колонка. Нужно для баз, которые старше самой колонки: запрос по
    /// отсутствующей колонке — не «ноль», а исключение, то есть сторож ронял бы старт вместо того,
    /// чтобы его проверить (той же ценой обошлась чистая установка в #1046).
    /// </summary>
    private static async Task<bool> HasColumnAsync(
        NpgsqlConnection conn, string table, string column, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND table_name = @t AND column_name = @c)", conn);
        cmd.Parameters.AddWithValue("t", table);
        cmd.Parameters.AddWithValue("c", column);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    /// <summary>Какие из нужных таблиц существуют в базе сейчас.</summary>
    private static async Task<HashSet<string>> PresentTablesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in new[] { Constructions, Sections, Sets, Objects, Types })
        {
            await using var cmd = new NpgsqlCommand("SELECT to_regclass(@name) IS NOT NULL", conn);
            cmd.Parameters.AddWithValue("name", "public." + table);
            if (await cmd.ExecuteScalarAsync(ct) is true) present.Add(table);
        }
        return present;
    }

    /// <summary>
    /// Сверить перепись до и после. Расхождение — исключение, то есть отказ старта.
    ///
    /// <para>Строка, посчитанная ДО и не посчитавшаяся ПОСЛЕ, — тоже расхождение: значит таблица
    /// исчезла, и дальше сторож ослеп бы молча. Обратное — строка появилась — нормально: миграция
    /// создала таблицу, которой раньше не было.</para>
    /// </summary>
    public static void EnsureUnchanged(MigrationCensus? before, MigrationCensus? after)
    {
        // Нечего сверять: справочника не было, миграция создаёт его с нуля.
        if (before is null) return;

        var problems = new List<string>();
        if (after is null)
        {
            problems.Add("справочник исчез целиком: таблиц строек, разделов и комплектов после миграции нет");
        }
        else
        {
            foreach (var (label, was) in before.Counts)
            {
                if (!after.Counts.TryGetValue(label, out var now))
                    problems.Add($"{label}: было {was}, а после миграции считать стало нечем — таблица исчезла");
                else if (was != now)
                    problems.Add($"{label}: было {was}, стало {now}");
            }
        }

        if (problems.Count == 0) return;

        throw new InvalidOperationException(
            "Миграция изменила состав справочника ядра — приложение остановлено (ТЗ CORE-31).\n  " +
            string.Join("\n  ", problems) + "\n" +
            "Половинчатый старт запрещён: на стройках и разделах стоит адресация комплектов, общих " +
            "данных, наборов и профилей уровней, и потеря здесь выглядит потом как «пропали " +
            "документы». Восстановите базу из резервной копии прежней версией и сообщите о находке.");
    }

    /// <summary>
    /// Перепись схемы МОДУЛЯ (задача A2a этапа 2, issue #1072): сколько строк лежит в её таблицах
    /// всего. <c>null</c> — схемы ещё нет: модуль включают первый раз, и сверять не с чем.
    ///
    /// <para>⚠️ Считается СУММА по схеме, а не строки по таблицам. Иначе переименование таблицы
    /// выглядело бы потерей («таблица исчезла»), и остановить старт могла бы обычная миграция
    /// модуля — а исправить это было бы нечем: перепись перечисляет не таблицы, а то, что нашла в
    /// базе. Ядро называет свои таблицы поимённо и такой беды не знает; у модуля их не знает никто,
    /// кроме самого модуля.</para>
    ///
    /// <para>⚠️ История миграций из счёта исключена: её строки добавляет сама миграция, и они росли
    /// бы каждым обновлением — то есть сверка сравнивала бы число с заведомо другим.</para>
    ///
    /// <para>Читается сырым SQL по той же причине, что и перепись ядра: до миграции модель и схема не
    /// совпадают по определению.</para>
    /// </summary>
    public static async Task<MigrationCensus?> ReadSchemaAsync(
        DbContext db, string schema, CancellationToken ct = default)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            try
            {
                await conn.OpenAsync(ct);
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InvalidCatalogName)
            {
                // Базы ещё нет — первый запуск; её создаст миграция ядра, которая идёт раньше.
                return null;
            }
        }

        try
        {
            await using var exists = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = @s)", conn);
            exists.Parameters.AddWithValue("s", schema);
            if (await exists.ExecuteScalarAsync(ct) is not true) return null;

            // Число строк в каждой таблице схемы — запросом, который строится из имён самих таблиц:
            // перечислить их в коде ядра нельзя, таблицы принадлежат модулю. query_to_xml — способ
            // выполнить count(*) по каждой из них одним запросом, без цикла и без динамического SQL
            // на нашей стороне.
            await using var cmd = new NpgsqlCommand(
                """
                -- sum() по bigint в Postgres даёт numeric, а не bigint: без приведения перепись
                -- падала бы приведением типа, то есть сторож ронял бы старт вместо проверки.
                SELECT coalesce(sum(rows), 0)::bigint FROM (
                    SELECT (xpath('/row/c/text()', query_to_xml(
                                format('SELECT count(*) AS c FROM %I.%I', table_schema, table_name),
                                false, true, '')))[1]::text::bigint AS rows
                      FROM information_schema.tables
                     WHERE table_schema = @s AND table_type = 'BASE TABLE'
                       AND table_name <> @history) t
                """, conn);
            cmd.Parameters.AddWithValue("s", schema);
            cmd.Parameters.AddWithValue("history", HistoryTable);

            var total = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
            return new MigrationCensus(new Dictionary<string, long>(StringComparer.Ordinal)
            {
                [$"строк в схеме {schema}"] = total,
            });
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
    }

    /// <summary>Имя таблицы истории миграций — исключается из переписи схемы модуля.</summary>
    private const string HistoryTable = "__EFMigrationsHistory";

    /// <summary>
    /// Сверить перепись схемы модуля до и после его миграции. Отказ — только если строк СТАЛО
    /// МЕНЬШЕ.
    ///
    /// <para>Сравнение односторонее нарочно, и это отличие от переписи ядра. Миграция модуля вправе
    /// добавить строк: разбить таблицу на две, перенести значения из колонки в отдельные строки,
    /// завести справочник с начальным содержимым. Требуй здесь равенства — такая миграция
    /// останавливала бы старт, а способа её провести не было бы вовсе: перепись считает то, что
    /// нашла в схеме, и «разрешить» ей ничего нельзя. Потеря же строк — то, ради чего сверка и
    /// заведена, и она односторонним сравнением видна.</para>
    ///
    /// <para>⚠️ Чего это НЕ видит, сказано вслух: миграция, потерявшая одну таблицу и наполнившая
    /// другую, даст в сумме больше — и пройдёт. Перепись по таблицам поимённо этого не решает
    /// (см. <see cref="ReadSchemaAsync" />), поэтому граница названа здесь, а не подразумевается.</para>
    /// </summary>
    public static void EnsureNothingLost(MigrationCensus? before, MigrationCensus? after, string schema)
    {
        if (before is null) return;

        var problems = new List<string>();
        if (after is null)
        {
            problems.Add($"схема «{schema}» после миграции не найдена вовсе");
        }
        else
        {
            foreach (var (label, was) in before.Counts)
            {
                if (!after.Counts.TryGetValue(label, out var now))
                    problems.Add($"{label}: было {was}, а после миграции считать стало нечем");
                else if (now < was)
                    problems.Add($"{label}: было {was}, стало {now}");
            }
        }

        if (problems.Count == 0) return;

        throw new InvalidOperationException(
            $"Миграция модуля потеряла данные из схемы «{schema}» — приложение остановлено (ТЗ CORE-31).\n  " +
            string.Join("\n  ", problems) + "\n" +
            "Половинчатый старт запрещён: строки модуля — это счета, накладные и разноска затрат, и " +
            "«поднялось, но данных меньше» обнаружилось бы отчётом, не сошедшимся с бухгалтерией. " +
            "Восстановите базу из резервной копии прежней версией и сообщите о находке.");
    }

    /// <summary>Что посчитано — одной строкой для журнала запуска.</summary>
    public string Describe() => string.Join(", ", Counts.Select(c => c.Key + " " + c.Value));
}
