using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BHS.CRG.Api.Modules;

// Восстановление одной таблицы модуля и её счётчиков (issue #1073, #1158).
public sealed partial class ModuleSchemaBackup
{
    /// <summary>
    /// Сколько строк уходит в базу одной командой. Не про скорость: одна команда на таблицу означала
    /// бы склейку ВСЕЙ таблицы в одну строку в памяти, рядом с уже лежащими там строками манифеста.
    /// </summary>
    private const int RowsPerCommand = 500;

    /// <summary>
    /// Куда складываются порции строк перед вставкой. Временная таблица соединения ядра, живёт до
    /// конца его транзакции — то есть ровно столько, сколько восстановление.
    /// </summary>
    private const string Stage = "pg_temp.module_restore_rows";

    /// <summary>Колонка таблицы модуля, как её описывает БАЗА.</summary>
    /// <param name="Identity"><c>a</c> — счётчик <c>GENERATED ALWAYS</c>, <c>d</c> — <c>BY DEFAULT</c>,
    /// пусто — не счётчик.</param>
    /// <param name="Computed">Значение считает база (<c>GENERATED ALWAYS AS (…)</c>): писать в такую
    /// колонку нельзя ни вставкой, ни обновлением.</param>
    /// <param name="Sequence">Последовательность, принадлежащая колонке, если есть.</param>
    private sealed record ColumnShape(string Name, string Identity, bool Computed, string? Sequence);

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
    /// <para>⚠️ <b>Пишутся только те колонки, которые есть И в копии, И в схеме, и которые не считает
    /// сама база</b> (issue #1158). Первая редакция писала все колонки таблицы разом
    /// (<c>SELECT *</c> из <c>jsonb_populate_recordset</c>), и колонка, которой в копии нет, получала
    /// явный <c>NULL</c>. А копия старше схемы — обычный случай: её сняли до обновления. Колонка
    /// <c>NOT NULL DEFAULT …</c>, добавленная миграцией модуля, роняла бы восстановление целиком,
    /// необязательная — молча затирала бы живое значение у существующих строк. Теперь у новой строки
    /// такая колонка получает значение по умолчанию, у существующей не трогается, и отчёт её
    /// называет. Вычисляемая колонка (<c>GENERATED ALWAYS AS</c>) в копии есть — <c>to_jsonb</c>
    /// отдаёт её как обычную, — но писать её база не даёт вовсе, ни вставкой, ни обновлением.</para>
    ///
    /// <para>⚠️ <b>Вставка — ОДНА команда при любом числе строк.</b> Внешний ключ таблицы на себя
    /// (иерархия) база проверяет в конце команды, и потомок, пришедший порцией раньше родителя,
    /// откатил бы восстановление. Порции поэтому складываются во временную таблицу, а в таблицу
    /// модуля уходят из неё разом. Так делается для каждой таблицы, а не только для иерархий: есть
    /// ли у таблицы ссылка на себя, знает база, а не модель, — объявить её модуль вправе и рукописной
    /// миграцией.</para>
    ///
    /// <para><c>null</c> в ответе — таблица пропущена, секции в отчёте у неё не будет.</para>
    /// </summary>
    private static async Task<(int Created, int Updated)?> RestoreTableAsync(
        DbTransaction transaction, ITable table, JsonElement[] rows, string code, List<string> warnings,
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
        var shape = await ReadShapeAsync(transaction, name, ct);
        var known = shape.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var inCopy = rows[0].EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        // Ключа в копии нет — слить строки не с чем, как и у таблицы без ключа вовсе. Так выглядит
        // копия, снятая до того, как модуль сменил ключ таблицы.
        if (key.Where(k => !inCopy.Contains(k)).ToList() is { Count: > 0 } keyless)
        {
            warnings.Add(
                $"Модуль «{code}»: таблица {table.Name} ({rows.Length}) в копии есть, но в её строках " +
                $"нет колонок первичного ключа — {string.Join(", ", keyless)}. Строки пропущены: " +
                "слить их с существующими не с чем.");
            return null;
        }

        // Колонки, которые в копии есть, а в нынешней схеме модуля их нет. Молчать об этом нельзя:
        // незнакомые ключи при разборе строки просто отбрасываются, то есть колонка пропала бы у
        // ВСЕХ строк, а отчёт назвал бы восстановление успешным (ревью PR #1108). Пропавшая таблица
        // оговорку получала, пропавшая колонка — нет; асимметрия и была дефектом.
        var lost = inCopy.Where(n => !known.Contains(n)).Order(StringComparer.Ordinal).ToList();
        if (lost.Count > 0)
            warnings.Add(
                $"Модуль «{code}», таблица {table.Name}: в копии есть колонки, которых в нынешней " +
                $"схеме модуля нет — {string.Join(", ", lost)}. Их значения не восстановлены. Так " +
                "выглядит копия, снятая более новой версией модуля: строки вернулись, часть данных в " +
                "них — нет.");

        // И обратная сторона — та, что встречается чаще: колонка в схеме есть, а в копии её нет.
        var writable = shape.Where(c => !c.Computed).ToList();
        var absent = writable.Where(c => !inCopy.Contains(c.Name)).Select(c => c.Name).ToList();
        if (absent.Count > 0)
            warnings.Add(
                $"Модуль «{code}», таблица {table.Name}: в копии нет колонок, которые есть в нынешней " +
                $"схеме модуля, — {string.Join(", ", absent)}. Так выглядит копия, снятая более " +
                "старой версией модуля. У строк, которых в системе не было, в этих колонках значения " +
                "по умолчанию; у существующих строк они оставлены как есть.");

        var written = writable.Where(c => inCopy.Contains(c.Name)).ToList();

        // В обновлении нет ключа (по нему строка и найдена) и нет счётчика GENERATED ALWAYS: его база
        // разрешает только вставить (OVERRIDING SYSTEM VALUE), а обновить — лишь на DEFAULT. У
        // существующей строки номер остаётся свой.
        var rest = written
            .Where(c => !key.Contains(c.Name, StringComparer.Ordinal) && c.Identity != "a")
            .Select(c => c.Name).ToList();

        // OVERRIDING SYSTEM VALUE — только когда пишется колонка-счётчик: без него вставка своего
        // значения в колонку GENERATED ALWAYS отказывает, а встречать этот отказ при восстановлении
        // после аварии незачем. Ставится по ответу базы, а не по модели: объявить счётчик модуль
        // может и рукописной миграцией.
        var sql =
            $"INSERT INTO {name} ({string.Join(", ", written.Select(c => Quote(c.Name)))})" +
            (written.Any(c => c.Identity.Length > 0) ? " OVERRIDING SYSTEM VALUE " : " ") +
            $"SELECT {string.Join(", ", written.Select(c => $"r.{Quote(c.Name)}"))} FROM {Stage} s " +
            $"CROSS JOIN LATERAL jsonb_populate_recordset(NULL::{name}, s.rows) r " +
            $"ON CONFLICT ({string.Join(", ", key.Select(Quote))}) DO " +
            (rest.Count > 0
                ? "UPDATE SET " + string.Join(", ", rest.Select(c => $"{Quote(c)} = EXCLUDED.{Quote(c)}"))
                // Обновлять нечего — таблица из одних ключевых колонок (связка «многие ко многим»):
                // DO NOTHING вместо отказа — то же самое по смыслу, строка уже такая.
                : "NOTHING") +
            " RETURNING (xmax = 0)";

        await StageAsync(transaction, rows, ct);

        int created = 0, updated = 0;
        await using (var cmd = Command(transaction, sql))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                if (reader.GetBoolean(0)) created++;
                else updated++;
            }
        }

        foreach (var column in shape)
        {
            if (column.Sequence is { } sequence)
                await AdvanceOwnedSequenceAsync(transaction, name, column.Name, sequence, ct);
        }

        return (created, updated);
    }

    /// <summary>
    /// Сложить строки таблицы во временную порциями.
    ///
    /// <para>Порциями, а не одной командой (ревью PR #1108). Строки и так лежат в памяти целиком —
    /// так устроен манифест, и у секций ядра то же самое, — но склейка всей таблицы в одну строку
    /// держала бы РЯДОМ с ними ещё две копии: UTF-16 у нас и UTF-8 у драйвера. Тот же довод, по
    /// которому архив собирается на диске, а не в памяти (см. <c>ExportToFileAsync</c>).</para>
    /// </summary>
    private static async Task StageAsync(DbTransaction transaction, JsonElement[] rows, CancellationToken ct)
    {
        // Одна временная таблица на всё восстановление: заводится первой таблицей модуля, чистится
        // перед каждой следующей, исчезает вместе с транзакцией.
        await using (var create = Command(transaction,
            "CREATE TEMP TABLE IF NOT EXISTS module_restore_rows (rows jsonb NOT NULL) ON COMMIT DROP"))
            await create.ExecuteNonQueryAsync(ct);
        await using (var clear = Command(transaction, $"TRUNCATE {Stage}"))
            await clear.ExecuteNonQueryAsync(ct);

        foreach (var batch in rows.Chunk(RowsPerCommand))
        {
            await using var cmd = Command(transaction, $"INSERT INTO {Stage} VALUES (CAST(@rows AS jsonb))");
            Param(cmd, "rows", "[" + string.Join(",", batch.Select(r => r.GetRawText())) + "]");
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Колонки таблицы, как их описывает база: что считает она сама и какие последовательности стоят
    /// за колонками (ревью PR #1108, issue #1158).
    ///
    /// <para>Спрашивается у БАЗЫ, а не у модели: модуль вправе объявить и счётчик, и вычисляемую
    /// колонку рукописной миграцией, а копия обязана возвращаться в ту базу, которая есть.</para>
    /// </summary>
    private static async Task<List<ColumnShape>> ReadShapeAsync(
        DbTransaction transaction, string name, CancellationToken ct)
    {
        await using var cmd = Command(transaction,
            "SELECT a.attname, CAST(a.attidentity AS text), a.attgenerated <> CAST('' AS \"char\"), " +
            "       pg_get_serial_sequence(@table, a.attname) " +
            "FROM pg_attribute a " +
            "WHERE a.attrelid = CAST(@table AS regclass) AND a.attnum > 0 AND NOT a.attisdropped " +
            "ORDER BY a.attnum");
        Param(cmd, "table", name);

        var columns = new List<ColumnShape>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            columns.Add(new ColumnShape(
                reader.GetString(0), reader.GetString(1), reader.GetBoolean(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));

        return columns;
    }
}
