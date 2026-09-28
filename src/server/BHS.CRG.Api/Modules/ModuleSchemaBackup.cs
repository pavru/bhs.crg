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
/// (<c>UseTransaction</c>) — ровно на две операции, копию и восстановление. Без этого копия не
/// снимок: между чтением схемы ядра и схемы модуля уместилась бы чужая запись, и в копию попал бы
/// счёт, ссылающийся на объект, которого в той же копии нет. А восстановление, откатившееся на
/// данных модуля, оставило бы у заказчика ядро из копии и счета прежние — состояние, которое от
/// исправного не отличить.</para>
/// </summary>
public sealed class ModuleSchemaBackup(ModuleRegistry registry, IServiceProvider scoped)
    : IModuleSchemaBackup
{
    public async Task<BackupModuleSchema[]> ReadAsync(DbTransaction transaction, CancellationToken ct)
    {
        var sections = new List<BackupModuleSchema>();

        foreach (var (module, schema) in WithSchema())
        {
            var (db, ours) = ModuleSchemaMigrator.Resolve(scoped, module.Code, schema);
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

                    var (created, updated) = await WriteRowsAsync(db, table, rows, ct);
                    stats.Add(new RestoreSectionStat(
                        $"Модуль «{module.Code}»: {table.Name}", created, updated));
                }
            }
            finally
            {
                if (ours) await db.DisposeAsync();
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
        if (db.Database.CurrentTransaction?.GetDbTransaction() == transaction) return;

        db.Database.SetDbConnection(transaction.Connection, contextOwnsConnection: false);
        db.Database.UseTransaction(transaction);
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
    /// Вставить строки таблицы модуля одной командой, обновляя те, что уже есть по первичному ключу.
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
    /// </summary>
    private static async Task<(int Created, int Updated)> WriteRowsAsync(
        ModuleDbContext db, ITable table, JsonElement[] rows, CancellationToken ct)
    {
        var name = $"{Quote(table.Schema!)}.{Quote(table.Name)}";
        var key = table.PrimaryKey?.Columns.Select(c => c.Name).ToList();

        var sql = new StringBuilder()
            .Append($"INSERT INTO {name} SELECT * FROM jsonb_populate_recordset(NULL::{name}, CAST(@rows AS jsonb))");

        if (key is { Count: > 0 })
        {
            var rest = table.Columns.Select(c => c.Name)
                .Where(c => !key.Contains(c, StringComparer.Ordinal)).ToList();

            sql.Append($" ON CONFLICT ({string.Join(", ", key.Select(Quote))}) DO ");
            sql.Append(rest.Count > 0
                ? "UPDATE SET " + string.Join(", ", rest.Select(c => $"{Quote(c)} = EXCLUDED.{Quote(c)}"))
                // Таблица из одних ключевых колонок (связка «многие ко многим»): обновлять нечего,
                // а DO NOTHING вместо отказа — то же самое по смыслу, строка уже такая.
                : "NOTHING");
        }

        sql.Append(" RETURNING (xmax = 0)");

        await using var cmd = Command(db, sql.ToString());
        var rowsParam = cmd.CreateParameter();
        rowsParam.ParameterName = "rows";
        rowsParam.Value = "[" + string.Join(",", rows.Select(r => r.GetRawText())) + "]";
        cmd.Parameters.Add(rowsParam);

        int created = 0, updated = 0;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.GetBoolean(0)) created++;
            else updated++;
        }

        return (created, updated);
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
