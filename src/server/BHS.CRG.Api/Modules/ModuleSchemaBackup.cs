using System.Data.Common;
using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

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
/// «нет общей транзакции ядро↔модуль» (<see cref="ModuleDbContext" />): таблицы модуля читаются и
/// пишутся командами на соединении ядра, в его транзакции — ровно в двух операциях, копии и
/// восстановлении. Без этого копия не снимок: между чтением схемы ядра и схемы модуля уместилась бы
/// чужая запись, и в копию попал бы счёт, ссылающийся на объект, которого в той же копии нет. А
/// восстановление, откатившееся на данных модуля, оставило бы у заказчика ядро из копии и счета
/// прежние — состояние, которое от исправного не отличить.</para>
///
/// <para><b>Контекст модуля при этом НЕ ТРОГАЕТСЯ</b> (issue #1158): от него берётся только модель —
/// какие таблицы есть и в каком порядке их класть. Первая редакция подключала сам контекст к
/// соединению ядра и возвращала обратно; каждая такая подмена оставляла след — контекст, собранный на
/// источнике данных, после возврата терял и источник, и пароль. Исключение, которое не надо снимать,
/// нельзя и забыть снять.</para>
/// </summary>
public sealed partial class ModuleSchemaBackup(ModuleRegistry registry, IServiceProvider scoped)
    : IModuleSchemaBackup
{
    public async Task<BackupModuleSchema[]> ReadAsync(
        DbTransaction transaction, List<string> warnings, CancellationToken ct)
    {
        var sections = new List<BackupModuleSchema>();

        foreach (var (module, schema) in WithSchema())
        {
            var (db, ours) = ModuleSchemaMigrator.Resolve(scoped, module.Code, schema);
            try
            {
                var tables = new List<BackupModuleTable>();
                foreach (var table in TablesInOrder(db, schema.Name))
                    tables.Add(new BackupModuleTable(table.Name, await ReadRowsAsync(transaction, table, ct)));

                sections.Add(new BackupModuleSchema(module.Code, schema.Name, [.. tables],
                    await ReadSequencesAsync(transaction, schema.Name, ct)));
            }
            finally
            {
                if (ours) await db.DisposeAsync();
            }
        }

        // Данные выключенного модуля в копию не входят — и молчать об этом нельзя (issue #1158). ТЗ
        // обещает, что они переживают выключение, то есть в базе они есть; копия без них выглядит
        // полной, паспорт исправен, а узнают о нехватке при восстановлении — после аварии. Снять их
        // нечем: модель выключенного модуля в контейнере не зарегистрирована, а его схема могла
        // отстать от сборки (миграции выключенного модуля не применяются).
        foreach (var module in registry.Disabled)
        {
            if (module.Schema is not { } schema) continue;

            var live = await NonEmptyTablesAsync(transaction, schema.Name, ct);
            if (live.Count > 0)
                warnings.Add(
                    $"Модуль «{module.Code}» выключен, а в его схеме «{schema.Name}» есть данные " +
                    $"({string.Join(", ", live)}). В копию они НЕ вошли: копия снимает схемы только " +
                    "включённых модулей. Чтобы копия была полной, включите модуль и снимите её снова.");
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
            try
            {
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

                    if (await RestoreTableAsync(transaction, table, rows, module.Code, warnings, ct)
                        is not { } counts) continue;

                    stats.Add(new RestoreSectionStat(
                        $"Модуль «{module.Code}»: {table.Name}", counts.Created, counts.Updated));
                }

                // ПОСЛЕ таблиц и независимо от того, были ли в них строки: счётчик, из которого
                // модуль берёт номера сам, ни к одной строке не привязан.
                await RestoreSequencesAsync(transaction, schema.Name, section.Sequences ?? [], ct);
            }
            finally
            {
                if (ours) await db.DisposeAsync();
            }
        }

        await WarnAboutAbsentAsync(data, transaction, warnings, ct);

        return stats;
    }

    /// <summary>
    /// Копия умела снимать схемы модулей, а данных включённого здесь модуля в ней нет — сказать об
    /// этом (issue #1158; обещано в <see cref="BackupManifest" /> у <c>ModuleData</c> с самого начала
    /// и не было сделано).
    ///
    /// <para>Так выглядит копия, снятая там, где модуль не был подключён. Ядро из неё придёт, а счета
    /// останутся прежними и будут ссылаться на объекты, которые восстановление только что вернуло к
    /// состоянию копии. Это не отказ — восстановление ничего не удаляет и здесь, — но и не то, что
    /// человек ждёт от слов «восстановлено».</para>
    ///
    /// <para>Только когда у модуля ЕСТЬ данные: без них сообщать не о чем, а оговорка, звучащая при
    /// каждом восстановлении, перестаёт значить что-либо.</para>
    /// </summary>
    private async Task WarnAboutAbsentAsync(
        BackupModuleSchema[] data, DbTransaction transaction, List<string> warnings, CancellationToken ct)
    {
        var inCopy = data.Select(s => s.Module).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (module, schema) in WithSchema())
        {
            if (inCopy.Contains(module.Code)) continue;

            var live = await NonEmptyTablesAsync(transaction, schema.Name, ct);
            if (live.Count > 0)
                warnings.Add(
                    $"В копии нет данных модуля «{module.Code}»: она снята там, где этот модуль не был " +
                    $"подключён. Его данные на этой установке ({string.Join(", ", live)}) остались как " +
                    "были — из копии пришло только остальное.");
        }
    }

    private IEnumerable<(IAppModule Module, ModuleSchema Schema)> WithSchema() => registry.Enabled
        .Where(m => m.Schema is not null)
        .Select(m => (m, m.Schema!));

    /// <summary>
    /// Таблицы схемы, в которых есть хоть одна строка, — по ответу БАЗЫ, без модели модуля.
    ///
    /// <para>Без модели потому, что спрашивают это и про выключенный модуль, у которого контекста в
    /// контейнере нет. Таблица истории миграций не в счёт: строки в ней есть у любой созданной схемы,
    /// и «данными модуля» они не являются.</para>
    /// </summary>
    private static async Task<List<string>> NonEmptyTablesAsync(
        DbTransaction transaction, string schema, CancellationToken ct)
    {
        var tables = new List<string>();
        await using (var list = Command(transaction,
            "SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "WHERE n.nspname = @schema AND c.relkind IN ('r', 'p') AND c.relname <> @history " +
            "ORDER BY c.relname"))
        {
            Param(list, "schema", schema);
            Param(list, "history", HistoryRepository.DefaultTableName);

            await using var reader = await list.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) tables.Add(reader.GetString(0));
        }

        var live = new List<string>();
        foreach (var table in tables)
        {
            await using var probe = Command(transaction,
                $"SELECT EXISTS (SELECT 1 FROM {Quote(schema)}.{Quote(table)})");
            if (await probe.ExecuteScalarAsync(ct) is true) live.Add(table);
        }

        return live;
    }

    /// <summary>
    /// Таблицы схемы модуля — от независимых к зависимым (Kahn).
    ///
    /// <para>Порядок нужен на восстановлении: внешние ключи ВНУТРИ схемы модуля разрешены (запрещены
    /// только сквозные, см. <see cref="ModuleSchemaMigrator.EnsureModelStaysInSchema" />), и позиция
    /// счёта не вставится раньше самого счёта. Снимаем в том же порядке — копия тогда читается
    /// сверху вниз и без него.</para>
    ///
    /// <para>Ссылку таблицы на себя (иерархия) пропускаем: сортировке она не мешает, а строки
    /// таблицы вставляются ОДНОЙ командой при любом их числе (см. <c>RestoreTableAsync</c>) — порядок
    /// внутри команды база выбирает сама, и родитель в той же команде её устроит. Замкнутый цикл из
    /// двух таблиц сортировке не поддаётся вовсе; такие таблицы дописываются в конец по имени, и
    /// восстановление честно упадёт на внешнем ключе, а не сделает вид, что порядок нашёлся.</para>
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
        DbTransaction transaction, ITable table, CancellationToken ct)
    {
        // Порядок строк в PostgreSQL без сортировки не определён, и две копии одних и тех же данных
        // выглядели бы разными файлами: сравнить их (и проверить круг «снял — восстановил» на
        // равенство) стало бы нечем.
        //
        // По первичному ключу, а не по самой строке (issue #1158): сортировка по to_jsonb(t) сравнивает
        // строки ЦЕЛИКОМ — на таблице позиций в десятки тысяч строк это сортировка на диске, причём при
        // каждой ночной копии и каждой оценке её размера. По ключу тот же порядок даёт индекс. Строка
        // целиком остаётся ключом сортировки только там, где ключа нет вовсе.
        var order = table.PrimaryKey?.Columns is { Count: > 0 } key
            ? string.Join(", ", key.Select(c => $"t.{Quote(c.Name)}"))
            : "1";
        await using var cmd = Command(transaction,
            $"SELECT to_jsonb(t) FROM {Quote(table.Schema!)}.{Quote(table.Name)} t ORDER BY {order}");

        var rows = new List<JsonElement>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            // Документ освобождаем сразу: его буфер взят из общего пула, а в копии живёт клон.
            using var row = JsonDocument.Parse(reader.GetString(0));
            rows.Add(row.RootElement.Clone());
        }

        return [.. rows];
    }

    private static void Param(DbCommand cmd, string name, object value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        cmd.Parameters.Add(parameter);
    }

    /// <summary>
    /// Команда на соединении ЯДРА, в его транзакции — то самое названное исключение из правила «нет
    /// общей транзакции». Контекст модуля в ней не участвует.
    /// </summary>
    private static DbCommand Command(DbTransaction transaction, string sql)
    {
        var cmd = transaction.Connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = transaction;
        return cmd;
    }

    /// <summary>
    /// Имя в кавычках. Имена приходят из модели модуля, а не от пользователя, но кавычка внутри
    /// имени — единственное, чем эта строка превращается в чужой запрос, и защита от неё стоит одну
    /// замену.
    /// </summary>
    private static string Quote(string name) => '"' + name.Replace("\"", "\"\"") + '"';
}
