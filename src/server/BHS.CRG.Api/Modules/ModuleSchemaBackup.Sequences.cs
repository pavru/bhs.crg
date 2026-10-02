using System.Data.Common;
using BHS.CRG.Application.Backup;

namespace BHS.CRG.Api.Modules;

// Последовательности схемы модуля в копии (ревью PR #1108, issue #1158).
//
// ⚠️ Общее правило обоих путей ниже: счётчик двигается ТОЛЬКО ВПЕРЁД. Первая редакция ставила его в
// наибольшее значение таблицы безусловно — и там, где он уже ушёл дальше, возвращала назад. Так бывает
// в живой системе: строки удалили, а на их идентификаторы ссылаются журнал и объекты ядра (внешних
// ключей сквозь схемы нет); новые строки получили бы те же идентификаторы. К тому же setval не входит
// в транзакцию и не откатывается вместе с восстановлением: сдвиг назад остался бы и после отказа.
// Сдвиг вперёд в худшем случае пропускает номера.
public sealed partial class ModuleSchemaBackup
{
    /// <summary>
    /// Состояние последовательностей схемы модуля — всех, а не только стоящих за колонками.
    ///
    /// <para>Так же поступает <c>pg_dump</c>: он переносит само состояние счётчика, а не выводит его
    /// из строк. Вывести можно только то, что за колонкой закреплено; номер счёта, который модуль
    /// берёт из своей последовательности кодом (или блоками, как HiLo), из таблицы не восстановить —
    /// и после восстановления на чистую установку он пошёл бы с начала, по уже выданным номерам.</para>
    ///
    /// <para>Счётчик, из которого ни разу не брали значение, в копию не попадает: возвращать нечего.
    /// Значение читается мимо снимка транзакции — последовательности в нём не участвуют — и потому
    /// может оказаться чуть ВПЕРЕДИ строк копии; это безвредно, как и любой сдвиг вперёд.</para>
    /// </summary>
    private static async Task<BackupModuleSequence[]> ReadSequencesAsync(
        DbTransaction transaction, string schema, CancellationToken ct)
    {
        await using var cmd = Command(transaction,
            "SELECT sequencename, last_value FROM pg_sequences " +
            "WHERE schemaname = @schema AND last_value IS NOT NULL ORDER BY sequencename");
        Param(cmd, "schema", schema);

        var sequences = new List<BackupModuleSequence>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            sequences.Add(new BackupModuleSequence(reader.GetString(0), reader.GetInt64(1)));

        return [.. sequences];
    }

    /// <summary>
    /// Вернуть счётчики схемы к состоянию копии — вперёд, если копия впереди.
    ///
    /// <para>Последовательность, которой в нынешней схеме нет, пропускается молча: модуль сменил
    /// способ нумерации, и прикладывать прежнее значение не к чему.</para>
    /// </summary>
    private static async Task RestoreSequencesAsync(
        DbTransaction transaction, string schema, BackupModuleSequence[] sequences, CancellationToken ct)
    {
        foreach (var sequence in sequences)
        {
            await using var cmd = Command(transaction,
                "SELECT setval(CAST(c.oid AS regclass), @value) " +
                "FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
                "WHERE n.nspname = @schema AND c.relname = @name AND c.relkind = 'S' " +
                $"  AND {Behind("CAST(c.oid AS regclass)", "@value")}");
            Param(cmd, "schema", schema);
            Param(cmd, "name", sequence.Name);
            Param(cmd, "value", sequence.LastValue);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Сдвинуть последовательность за колонкой за наибольшее восстановленное значение
    /// (ревью PR #1108).
    ///
    /// <para>Нужен и при состоянии счётчиков в копии: оно появилось позже самих копий (issue #1158), и
    /// строки с идентификаторами бывают вставлены мимо счётчика. Без сдвига строки возвращаются, а
    /// счётчик остаётся там, где стоял на пустой таблице: первая же вставка модуля после
    /// восстановления отказывает дублем ключа — далеко от восстановления и без всякой связи с
    /// ним.</para>
    /// </summary>
    private static async Task AdvanceOwnedSequenceAsync(
        DbTransaction transaction, string table, string column, string sequence, CancellationToken ct)
    {
        await using var cmd = Command(transaction,
            "SELECT setval(CAST(@seq AS regclass), m.top) " +
            $"FROM (SELECT max({Quote(column)}) AS top FROM {table}) m " +
            $"WHERE m.top IS NOT NULL AND {Behind("CAST(@seq AS regclass)", "m.top")}");
        Param(cmd, "seq", sequence);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Условие «счётчик отстал от значения». <c>pg_sequence_last_value</c> отвечает <c>NULL</c>, пока
    /// из счётчика ни разу не брали, — такой отстал от любого значения.
    /// </summary>
    private static string Behind(string sequence, string value) =>
        $"COALESCE(pg_sequence_last_value({sequence}), {value} - 1) < {value}";
}
