using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Objects;
using BHS.CRG.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Backup;

/// <summary>
/// Тёзки типов при восстановлении — часть <see cref="BackupService" /> (issue #1400).
///
/// <para>Типы восстанавливаются по идентификатору. Но часть типов система заводит сама, при первом
/// старте: проекция типов модулей ставит «СтатьяВнеСтроек» под СВОИМ случайным идентификатором. Копия
/// системы с тем же модулем несёт тот же код под идентификатором исходной системы — для восстановления
/// это новый тип, вставка, и уникальный индекс по коду отказывал. Откатывалось всё восстановление:
/// переезд на новый сервер копией у системы со счетами не работал вовсе.</para>
///
/// <para><b>Правило.</b> Здешний тёзка, на которого ничто не ссылается, уступает место типу из копии:
/// копия — то, что переносят, а у тёзки нет ничего, что можно потерять. Тёзку, у которого есть данные,
/// восстановление не трогает и отказывает сразу, назвав тип и держателя: слить два разных типа под
/// одним кодом оно не умеет, а невнятный отказ базы о ключе ничего не говорит администратору.</para>
/// </summary>
public partial class BackupService
{
    private async Task ReplaceTypeNamesakesAsync(
        BackupDocumentType[] items, List<string> warnings, CancellationToken ct)
    {
        var copyIds = items.Select(i => i.Id).ToHashSet();
        var here = await db.DocumentTypes.AsNoTracking()
            .Select(t => new { t.Id, t.Code, t.Name })
            .ToListAsync(ct);

        foreach (var item in items)
        {
            // Сравнение — как у уникального индекса, по точному коду: отказывал именно он.
            var namesake = here.FirstOrDefault(t => t.Code == item.Code && t.Id != item.Id);
            // Здешний тип есть в копии под ДРУГИМ кодом — это переименование, а не тёзка: его строку
            // восстановление обновит само.
            if (namesake is null || copyIds.Contains(namesake.Id)) continue;

            if (await HolderOfAsync(holders, namesake.Id, ct) is { } holder)
                throw new ConflictException(Occupied(item, holder));

            await db.DocumentTypes.Where(t => t.Id == namesake.Id).ExecuteDeleteAsync(ct);
            warnings.Add($"Тип «{namesake.Name}» этой системы заменён одноимённым из копии (код {item.Code}): " +
                         "данных у здешнего не было.");
        }
    }

    private static string Occupied(BackupDocumentType item, string holder) =>
        $"Тип «{item.Name}» (код {item.Code}) есть и в копии, и на этой системе — под разными " +
        $"идентификаторами, и здешний уже занят: на него ссылается {holder}. Восстановление поверх " +
        "смешало бы два разных типа под одним кодом. Восстановите копию на пустую систему или смените " +
        "код здешнего типа в редакторе типов.";

    /// <summary>
    /// Кто держит здешний тип, — словами, или <c>null</c>.
    ///
    /// <para>Сначала порт держателей в модулях (<see cref="IRecordHolders" />), как у всякого удаления
    /// записи ядра: данные модулей — в том числе выключенного — он находит по схеме модулей и называет
    /// словами. Не ответил — удалять нельзя: «проверить не удалось» не значит «никто не держит».</para>
    /// </summary>
    private async Task<string?> HolderOfAsync(IRecordHolders modules, Guid typeId, CancellationToken ct)
    {
        var held = await modules.FindAsync([typeId], ct);
        if (held.Any) return held.IsUnverified ? "проверить не удалось: " + held.Lines[0] : held.Lines[0];
        return await CoreHolderOfAsync(typeId, ct);
    }

    /// <summary>
    /// Где в базе упомянут идентификатор, — «схема.таблица.колонка» первого держателя, или <c>null</c>.
    ///
    /// <para>По схеме, а не по списку держателей: на тип ссылаются колонки ядра (объекты, шаблоны,
    /// планы комплектов, привязки) И таблицы модулей (счёт — своей колонкой), о которых ядро не знает
    /// ничего. Список, выписанный руками, пропустил бы держателя — и тип с данными был бы удалён.
    /// Тот же приём, что у сбора живых путей хранилища (<c>LiveBlobPathScan</c>): колонки-идентификаторы
    /// сравниваются точно, текст и JSON — по вхождению строки.</para>
    ///
    /// <para>Цена — проход по таблицам целиком. Платится только при тёзке, то есть на чужой для копии
    /// системе: на пустой проверяется тип, заведённый при старте, и таблицы почти пусты.</para>
    /// </summary>
    private async Task<string?> CoreHolderOfAsync(Guid typeId, CancellationToken ct)
    {
        var columns = await db.Database.SqlQuery<HolderColumn>($"""
            SELECT table_schema AS "Schema", table_name AS "Table", column_name AS "Column",
                   data_type = 'uuid' AS "IsUuid"
            FROM information_schema.columns c
            WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
              AND data_type IN ('uuid', 'jsonb', 'json', 'text', 'character varying', 'ARRAY')
              AND NOT (table_schema = 'public' AND table_name = 'document_types' AND column_name = 'Id')
              AND EXISTS (SELECT 1 FROM information_schema.tables t
                          WHERE t.table_schema = c.table_schema AND t.table_name = c.table_name
                            AND t.table_type = 'BASE TABLE')
            ORDER BY 1, 2, 3
            """).ToListAsync(ct);

        var text = typeId.ToString();
        foreach (var c in columns)
        {
            var target = $"{Quoted(c.Schema)}.{Quoted(c.Table)}";
            var found = c.IsUuid
                ? await db.Database.SqlQueryRaw<bool>(
                    $"SELECT EXISTS (SELECT 1 FROM {target} WHERE {Quoted(c.Column)} = {{0}}) AS \"Value\"", typeId)
                    .SingleAsync(ct)
                : await db.Database.SqlQueryRaw<bool>(
                    $"SELECT EXISTS (SELECT 1 FROM {target} WHERE {Quoted(c.Column)}::text LIKE {{0}}) AS \"Value\"",
                    "%" + text + "%")
                    .SingleAsync(ct);
            if (found) return $"{c.Schema}.{c.Table}.{c.Column}";
        }
        return null;
    }

    private static string Quoted(string name) => '"' + name.Replace("\"", "\"\"") + '"';

    private sealed record HolderColumn(string Schema, string Table, string Column, bool IsUuid);
}
