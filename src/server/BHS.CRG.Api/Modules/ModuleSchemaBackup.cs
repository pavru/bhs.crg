using System.Data.Common;
using System.Text;
using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace BHS.CRG.Api.Modules;

/// <summary>
/// Схемы модулей в резервной копии — реализация порта <see cref="IModuleSchemaBackup" /> (задача A2b
/// этапа 2, issue #1073, ТЗ CORE-4, CORE-29).
///
/// <para>Живёт в корне композиции по той же причине, что и <see cref="ModuleSchemaMigrator" />: это
/// единственное место, где известны и ядро, и модули. Инфраструктура на контракты модулей ссылаться
/// не может — обратная ссылка сделала бы её модулем в смысле <c>ModuleBoundaryTests</c>.</para>
///
/// <para><b>Состав модуля не перечисляется, а спрашивается.</b> Таблицы берутся из реляционной
/// модели его контекста, строки — как <c>to_jsonb(таблица)</c>, обратно кладёт
/// <c>jsonb_populate_recordset</c>. Поэтому первая таблица счетов (C1, #1076) попадёт в копию и
/// вернётся из неё без единой правки здесь, а ядро по-прежнему не знает, из чего состоит модуль.
/// Записью значений занимается сама база — та же, которой пользуется <c>pg_dump</c>, — так что
/// <c>jsonb</c>, массив, <c>bytea</c> и время возвращаются теми же.</para>
///
/// <para><b>⚠️ Общая транзакция здесь есть, и это названное исключение</b> из правила
/// «нет общей транзакции ядро↔модуль» (<see cref="ModuleDbContext" />). Контекст модуля подключается
/// к соединению ядра (<c>SetDbConnection</c>) и входит в его транзакцию
/// (<c>UseTransaction</c>) — ровно на две операции, копию и восстановление, и вместе с ними
/// исключение КОНЧАЕТСЯ (<see cref="Release" />). Без этого копия не снимок: между чтением схемы ядра и схемы модуля уместилась бы чужая запись, и в копию попал бы
/// счёт, ссылающийся на объект, которого в той же копии нет. А восстановление, откатившееся на
/// данных модуля, оставило бы у заказчика ядро из копии и счета прежние — состояние, которое от
/// исправного не отличить.</para>
/// </summary>
public sealed class ModuleSchemaBackup(ModuleRegistry registry, IServiceProvider scoped)
    : IModuleSchemaBackup
{
    /// <summary>
    /// Сколько строк уходит в базу одной командой. Не про скорость: одна команда на таблицу означала
    /// бы склейку ВСЕЙ таблицы в одну строку в памяти, рядом с уже лежащими там строками манифеста.
    /// </summary>
    private const int RowsPerCommand = 500;

    public async Task<BackupModuleSchema[]> ReadAsync(DbTransaction transaction, CancellationToken ct)
    {
        var sections = new List<BackupModuleSchema>();

        foreach (var (module, schema) in WithSchema())
        {
            var (db, ours) = ModuleSchemaMigrator.Resolve(scoped, module.Code, schema);
            var connection = db.Database.GetConnectionString();
            try
            {
                Enlist(db, transaction);

                var tables = new List<BackupModuleTable>();
                foreach (var table in TablesInOrder(db, schema.Name))
                    tables.Add(new BackupModuleTable(table.Name, await ReadRowsAsync(db, table, ct)));

                sections.Add(new BackupModuleSchema(module.Code, schema.Name, [.. tables]));
            }
            finally
            {
                if (ours) await db.DisposeAsync();
                else Release(db, connection);
            }
        }

        return [.. sections];
    }

    public async Task<IReadOnlyList<RestoreSectionStat>> RestoreAsync(
        BackupModuleSchema[] data, DbTransaction transaction, List<string> warnings,
        CancellationToken ct)
    {
        var stats = new List<RestoreSectionStat>();

        foreach (var section in data)
        {
            var module = registry.Find(section.Module);
            if (module?.Schema is not { } schema)
            {
                // Данные в копии есть, приложить их некуда: модуль выключен или этой сборке
                // неизвестен. Молчать нельзя — «счетов после восстановления нет» человек прочитает
                // как потерю данных, а не как состав поставки.
                warnings.Add(
                    $"Данные модуля «{section.Module}» (схема «{section.Schema}») в копии есть, но на " +
                    "этом экземпляре модуль не подключён — они пропущены. Включите модуль и " +
                    "восстановите копию снова: в копии они остались.");
                continue;
            }

            if (!string.Equals(schema.Name, section.Schema, StringComparison.Ordinal))
            {
                // Модуль переименовал схему между версиями. Класть данные в новую схему по одному
                // совпадению кода модуля нельзя: соответствие таблиц никто не объявлял, и
                // «восстановилось» означало бы, что данные легли куда попало.
                warnings.Add(
                    $"Модуль «{section.Module}» в копии владел схемой «{section.Schema}», а в этой " +
                    $"сборке владеет «{schema.Name}» — данные пропущены. Перенос между схемами " +
                    "делает миграция модуля, а не восстановление копии.");
                continue;
            }

            var (db, ours) = ModuleSchemaMigrator.Resolve(scoped, module.Code, schema);
            var connection = db.Database.GetConnectionString();
            try
            {
                Enlist(db, transaction);

                var tables = TablesInOrder(db, schema.Name);
                var known = tables.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

                var gone = section.Tables.Where(t => !known.Contains(t.Table) && t.Rows.Length > 0)
                    .Select(t => $"{t.Table} ({t.Rows.Length})").ToList();
                if (gone.Count > 0)
                    warnings.Add(
                        $"Модуль «{module.Code}»: в копии есть таблицы, которых в нынешней модели " +
                        $"модуля нет — {string.Join(", ", gone)}. Строки пропущены: куда их класть, " +
                        "знает только миграция модуля.");

                // Порядок вставки берётся у НЫНЕШНЕЙ модели, а не из копии: в копии он был таким,
                // каким был состав модуля тогда, а внешние ключи проверяет сегодняшняя база.
                foreach (var table in tables)
                {
                    var rows = section.Tables.FirstOrDefault(
                        t => string.Equals(t.Table, table.Name, StringComparison.Ordinal))?.Rows ?? [];
                    if (rows.Length == 0) continue;

                    if (await RestoreTableAsync(db, table, rows, module.Code, warnings, ct)
                        is not { } counts) continue;

                    stats.Add(new RestoreSectionStat(
                        $"Модуль «{module.Code}»: {table.Name}", counts.Created, counts.Updated));
                }
            }
            finally
            {
                if (ours) await db.DisposeAsync();
                else Release(db, connection);
            }
        }

        return stats;
    }

    private IEnumerable<(IAppModule Module, ModuleSchema Schema)> WithSchema() => registry.Enabled
        .Where(m => m.Schema is not null)
        .Select(m => (m, m.Schema!));

    /// <summary>
    /// Подключить контекст модуля к соединению и транзакции ядра — то самое названное исключение из
    /// правила «нет общей транзакции».
    ///
    /// <para><c>contextOwnsConnection: false</c> обязательно: соединение принадлежит контексту ядра,
    /// и закрой его контекст модуля при освобождении — упало бы всё, что идёт в этой области
    /// дальше, включая коммит.</para>
    /// </summary>
    private static void Enlist(ModuleDbContext db, DbTransaction transaction)
    {
        // Транзакцию отпускаем прежде подмены: занятое соединение EF менять отказывается. Своей
        // транзакции у контекста здесь быть не должно, но копию снимают и из середины запроса.
        db.Database.UseTransaction(null);
        db.Database.SetDbConnection(transaction.Connection, contextOwnsConnection: false);
        db.Database.UseTransaction(transaction);
    }

    /// <summary>
    /// Вернуть контекст модуля СЕБЕ: исключение «одна транзакция» кончается вместе с копией
    /// (ревью PR #1108).
    ///
    /// <para>Контекст из контейнера живёт всю область запроса, а не одну операцию. Оставь его на
    /// соединении ядра — и всё, что модуль запишет в этой области ПОСЛЕ копии, молча войдёт в
    /// транзакцию ядра: ровно то, что правило «нет общей транзакции» запрещает, и заметить это нечем —
    /// запросы работают, данные сохраняются, общая транзакция просто есть.</para>
    ///
    /// <para>⚠️ Крах при этом не наступает, и проверять надо было не его: <c>SetDbConnection</c> второй
    /// раз проходит — своё соединение контекст модуля к тому моменту закрыл (проверено прямо). Дефект
    /// тихий, поэтому и сторож смотрит на состояние.</para>
    ///
    /// <para>⚠️ Строку подключения приходится возвращать ОТДЕЛЬНО: <c>SetDbConnection(null)</c> снимает
    /// соединение, но к строке из настроек не возвращается — контекст остаётся вовсе без адреса, и
    /// следующий же его запрос отказывает словами «The ConnectionString property has not been
    /// initialized» (проверено прогоном: первая редакция этой правки так и падала). Порядок важен:
    /// транзакцию отпускаем прежде соединения, иначе EF отказывается менять занятое.</para>
    /// </summary>
    private static void Release(ModuleDbContext db, string? connectionString)
    {
        db.Database.UseTransaction(null);
        db.Database.SetDbConnection(null);
        if (connectionString is not null) db.Database.SetConnectionString(connectionString);
    }

    /// <summary>
    /// Таблицы схемы модуля — от независимых к зависимым (Kahn).
    ///
    /// <para>Порядок нужен на восстановлении: внешние ключи ВНУТРИ схемы модуля разрешены (запрещены
    /// только сквозные, см. <see cref="ModuleSchemaMigrator.EnsureModelStaysInSchema" />), и позиция
    /// счёта не вставится раньше самого счёта. Снимаем в том же порядке — копия тогда читается
    /// сверху вниз и без него.</para>
    ///
    /// <para>Ссылку таблицы на себя (иерархия) пропускаем: сортировке она не мешает, а строки такой
    /// таблицы вставляются одной командой — порядок внутри команды база выбирает сама, и родитель в
    /// той же команде её устроит. Замкнутый цикл из двух таблиц сортировке не поддаётся вовсе; такие
    /// таблицы дописываются в конец по имени, и восстановление честно упадёт на внешнем ключе, а не
    /// сделает вид, что порядок нашёлся.</para>
    /// </summary>
    private static List<ITable> TablesInOrder(ModuleDbContext db, string schema)
    {
        var tables = db.Model.GetRelationalModel().Tables
            .Where(t => string.Equals(t.Schema, schema, StringComparison.Ordinal))
            .ToList();

        var byName = tables.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var waitingFor = tables.ToDictionary(
            t => t.Name,
            t => t.ForeignKeyConstraints
                .Select(fk => fk.PrincipalTable.Name)
                .Where(n => n != t.Name && byName.ContainsKey(n))
                .ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

        var ordered = new List<ITable>();
        var placed = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            var ready = waitingFor
                .Where(p => !placed.Contains(p.Key) && p.Value.All(placed.Contains))
                .Select(p => p.Key)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            if (ready.Count == 0) break;

            foreach (var name in ready)
            {
                ordered.Add(byName[name]);
                placed.Add(name);
            }
        }

        ordered.AddRange(tables
            .Where(t => !placed.Contains(t.Name))
            .OrderBy(t => t.Name, StringComparer.Ordinal));

        return ordered;
    }

    private static async Task<JsonElement[]> ReadRowsAsync(
        ModuleDbContext db, ITable table, CancellationToken ct)
    {
        // ORDER BY 1 — по самой строке. Порядок строк в PostgreSQL без сортировки не определён, и
        // две копии одних и тех же данных выглядели бы разными файлами: сравнить их (и проверить
        // круг «снял — восстановил» на равенство) стало бы нечем.
        await using var cmd = Command(db,
            $"SELECT to_jsonb(t) FROM {Quote(table.Schema!)}.{Quote(table.Name)} t ORDER BY 1");

        var rows = new List<JsonElement>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(JsonDocument.Parse(reader.GetString(0)).RootElement.Clone());

        return [.. rows];
    }

    /// <summary>
    /// Вернуть строки одной таблицы модуля: вставить новые, обновить существующие по первичному ключу.
    ///
    /// <para><b>Что будет при столкновении по ДРУГОМУ уникальному ключу</b> (например номер счёта):
    /// восстановление упадёт, и вся транзакция откатится. Это решение, а не недосмотр: ядро
    /// разбирает такие столкновения поимённо — знает, что позицию перечня работ определяет
    /// естественный ключ (<c>BackupService.Restore.WorkPlan.cs</c>), — а про таблицы модуля ядро
    /// не знает ничего и разобрать не может. Тихое слияние по догадке склеило бы два разных счёта в
    /// один; отказ говорит правду: копию модуля нельзя влить в систему, где уже есть другие его
    /// данные с теми же естественными ключами.</para>
    ///
    /// <para><c>xmax = 0</c> отличает вставленную строку от обновлённой — так отчёт называет, что
    /// именно сделал, а не «строк: 12».</para>
    ///
    /// <para>⚠️ <b>Таблица без первичного ключа не восстанавливается</b>, и об этом говорится вслух
    /// (ревью PR #1108). Слить её строки не с чем: голая вставка удвоила бы их при каждом повторном
    /// восстановлении — а повторное восстановление той же копии в работе дело обычное. Из двух
    /// неверных исходов «не вернулось и сказано» лучше, чем «вернулось дважды и молча»: удвоенные
    /// строки уже не различить, а копия никуда не делась. Требование к модулю названо в тексте
    /// оговорки.</para>
    ///
    /// <para><c>null</c> в ответе — таблица пропущена, секции в отчёте у неё не будет.</para>
    /// </summary>
    private static async Task<(int Created, int Updated)?> RestoreTableAsync(
        ModuleDbContext db, ITable table, JsonElement[] rows, string code, List<string> warnings,
        CancellationToken ct)
    {
        var name = $"{Quote(table.Schema!)}.{Quote(table.Name)}";

        if (table.PrimaryKey?.Columns is not { Count: > 0 } keyColumns)
        {
            warnings.Add(
                $"Модуль «{code}»: таблица {table.Name} ({rows.Length}) в копии есть, но у неё нет " +
                "первичного ключа — строки пропущены. Слить их не с чем, а вставка без слияния удвоила " +
                "бы их при повторном восстановлении. Чтобы таблица модуля восстанавливалась, у неё " +
                "должен быть первичный ключ.");
            return null;
        }

        var key = keyColumns.Select(c => c.Name).ToList();
        var columns = table.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

        // Колонки, которые в копии есть, а в нынешней модели модуля их нет. Молчать об этом нельзя:
        // jsonb_populate_recordset незнакомые ключи просто игнорирует, то есть колонка пропала бы у
        // ВСЕХ строк, а отчёт назвал бы восстановление успешным (ревью PR #1108). Пропавшая таблица
        // оговорку получала, пропавшая колонка — нет; асимметрия и была дефектом.
        var lost = rows[0].EnumerateObject().Select(p => p.Name)
            .Where(n => !columns.Contains(n))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (lost.Count > 0)
            warnings.Add(
                $"Модуль «{code}», таблица {table.Name}: в копии есть колонки, которых в нынешней " +
                $"модели модуля нет — {string.Join(", ", lost)}. Их значения не восстановлены. Так " +
                "выглядит копия, снятая более новой версией модуля: строки вернулись, часть данных в " +
                "них — нет.");

        var generated = await ReadGeneratedAsync(db, table, name, ct);

        var rest = table.Columns.Select(c => c.Name)
            .Where(c => !key.Contains(c, StringComparer.Ordinal)).ToList();

        // OVERRIDING SYSTEM VALUE — только когда у таблицы есть колонка-счётчик: без него вставка
        // своего значения в колонку GENERATED ALWAYS отказывает, а встречать этот отказ при
        // восстановлении после аварии незачем. Ставится по ответу базы, а не по модели: объявить
        // счётчик модуль может и рукописной миграцией.
        var sql =
            $"INSERT INTO {name}{(generated.Identity ? " OVERRIDING SYSTEM VALUE" : string.Empty)} " +
            $"SELECT * FROM jsonb_populate_recordset(NULL::{name}, CAST(@rows AS jsonb)) " +
            $"ON CONFLICT ({string.Join(", ", key.Select(Quote))}) DO " +
            (rest.Count > 0
                ? "UPDATE SET " + string.Join(", ", rest.Select(c => $"{Quote(c)} = EXCLUDED.{Quote(c)}"))
                // Таблица из одних ключевых колонок (связка «многие ко многим»): обновлять нечего,
                // а DO NOTHING вместо отказа — то же самое по смыслу, строка уже такая.
                : "NOTHING") +
            " RETURNING (xmax = 0)";

        int created = 0, updated = 0;

        // Порциями, а не всей таблицей одной командой (ревью PR #1108). Строки и так лежат в памяти
        // целиком — так устроен манифест, и у секций ядра то же самое, — но склейка всей таблицы в
        // одну строку держала бы РЯДОМ с ними ещё две копии: UTF-16 у нас и UTF-8 у драйвера. Тот же
        // довод, по которому архив собирается на диске, а не в памяти (см. ExportToFileAsync).
        foreach (var batch in rows.Chunk(RowsPerCommand))
        {
            await using var cmd = Command(db, sql);
            Param(cmd, "rows", "[" + string.Join(",", batch.Select(r => r.GetRawText())) + "]");

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.GetBoolean(0)) created++;
                else updated++;
            }
        }

        await AdvanceSequencesAsync(db, name, generated.Sequences, ct);

        return (created, updated);
    }

    /// <summary>
    /// Что у таблицы генерирует база: есть ли колонка-счётчик и какие последовательности стоят за
    /// колонками (ревью PR #1108).
    ///
    /// <para>Спрашивается у БАЗЫ, а не у модели: модуль вправе объявить счётчик и рукописной
    /// миграцией, а копия обязана возвращаться в ту базу, которая есть.</para>
    /// </summary>
    private static async Task<(bool Identity, List<(string Column, string Sequence)> Sequences)>
        ReadGeneratedAsync(ModuleDbContext db, ITable table, string name, CancellationToken ct)
    {
        await using var cmd = Command(db,
            "SELECT a.attname, a.attidentity <> CAST('' AS \"char\") AS identity, " +
            "       pg_get_serial_sequence(@table, a.attname) AS sequence " +
            "FROM pg_attribute a " +
            "WHERE a.attrelid = CAST(@table AS regclass) AND a.attnum > 0 AND NOT a.attisdropped");
        Param(cmd, "table", name);

        var identity = false;
        var sequences = new List<(string, string)>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.GetBoolean(1)) identity = true;
            if (!reader.IsDBNull(2)) sequences.Add((reader.GetString(0), reader.GetString(2)));
        }

        return (identity, sequences);
    }

    /// <summary>
    /// Сдвинуть последовательности за наибольшее восстановленное значение (ревью PR #1108).
    ///
    /// <para>Без этого строки возвращаются, а счётчик остаётся там, где стоял на пустой таблице: первая
    /// же вставка модуля после восстановления отказывает дублем ключа — далеко от восстановления и без
    /// всякой связи с ним. Так же поступает <c>pg_dump</c>, и по той же причине.</para>
    ///
    /// <para>Третий довод <c>setval</c> («счётчик уже использован») снимается на пустой таблице: иначе
    /// следующее значение оказалось бы вторым, а не первым.</para>
    /// </summary>
    private static async Task AdvanceSequencesAsync(
        ModuleDbContext db, string name, List<(string Column, string Sequence)> sequences,
        CancellationToken ct)
    {
        foreach (var (column, sequence) in sequences)
        {
            await using var cmd = Command(db,
                $"SELECT setval(CAST(@seq AS regclass), COALESCE(max({Quote(column)}), 1), " +
                $"max({Quote(column)}) IS NOT NULL) FROM {name}");
            Param(cmd, "seq", sequence);
            await cmd.ExecuteScalarAsync(ct);
        }
    }

    private static void Param(DbCommand cmd, string name, string value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        cmd.Parameters.Add(parameter);
    }

    private static DbCommand Command(ModuleDbContext db, string sql)
    {
        var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        return cmd;
    }

    /// <summary>
    /// Имя в кавычках. Имена приходят из модели модуля, а не от пользователя, но кавычка внутри
    /// имени — единственное, чем эта строка превращается в чужой запрос, и защита от неё стоит одну
    /// замену.
    /// </summary>
    private static string Quote(string name) => '"' + name.Replace("\"", "\"\"") + '"';
}
