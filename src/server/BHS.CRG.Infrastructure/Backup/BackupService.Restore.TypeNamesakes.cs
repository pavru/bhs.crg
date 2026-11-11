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

    /// <summary>Собственная колонка типа: в ней идентификатор — сам тип, а не ссылка на него.</summary>
    private static readonly (string Table, string Column) TypeIdColumn = ("document_types", "Id");

    /// <summary>
    /// Кто держит здешний тип, — словами, или <c>null</c>.
    ///
    /// <para>Сначала порт держателей в модулях (<see cref="IRecordHolders" />), как у всякого удаления
    /// записи ядра: данные модулей — в том числе выключенного — он находит по схеме модулей и называет
    /// словами. Не ответил — удалять нельзя: «проверить не удалось» не значит «никто не держит».</para>
    /// </summary>
    private async Task<string?> HolderOfAsync(IRecordHolders modules, Guid id, CancellationToken ct)
    {
        var held = await modules.FindAsync([id], ct);
        if (held.Any) return held.IsUnverified ? "проверить не удалось: " + held.Lines[0] : held.Lines[0];
        return (await CoreHolderOfAsync([id], TypeIdColumn, history: [], ct))?.Address;
    }

    /// <summary>
    /// Где в базе упомянут один из идентификаторов, — он сам и «схема.таблица.колонка» первого
    /// держателя, или <c>null</c>.
    ///
    /// <para>По схеме, а не по списку держателей: на тип ссылаются колонки ядра (объекты, шаблоны,
    /// планы комплектов, привязки) И таблицы модулей (счёт — своей колонкой), о которых ядро не знает
    /// ничего. Список, выписанный руками, пропустил бы держателя — и тип с данными был бы удалён.
    /// Тот же приём, что у сбора живых путей хранилища (<c>LiveBlobPathScan</c>): колонки-идентификаторы
    /// сравниваются точно, текст и JSON — по вхождению строки, без учёта регистра: GUID, записанный
    /// заглавными, — та же ссылка, и пропустить её значило бы оборвать её молча (ревью PR #1896).</para>
    ///
    /// <para><paramref name="own" /> — собственная колонка записи (<c>public</c>): строка самой записи
    /// держателем не считается. <paramref name="history" /> — таблицы <c>public</c>, где упоминание —
    /// история, а не ссылка (у профилей — журнал, задачи, уведомления; у типов — ни одной).</para>
    ///
    /// <para>Цена — проход по таблицам целиком, ОДИН на все спрошенные идентификаторы: запрос на
    /// колонку отвечает «есть ли хоть один», и только найденный уточняется. Платится лишь при тёзке, то
    /// есть на чужой для копии системе.</para>
    /// </summary>
    private async Task<(Guid Id, string Address)?> CoreHolderOfAsync(
        IReadOnlyList<Guid> ids, (string Table, string Column) own, string[] history, CancellationToken ct)
    {
        if (ids.Count == 0) return null;
        var (ownTable, ownColumn) = own;
        var columns = await db.Database.SqlQuery<HolderColumn>($"""
            SELECT table_schema AS "Schema", table_name AS "Table", column_name AS "Column",
                   data_type = 'uuid' AS "IsUuid"
            FROM information_schema.columns c
            WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
              AND data_type IN ('uuid', 'jsonb', 'json', 'text', 'character varying', 'ARRAY')
              AND NOT (table_schema = 'public' AND table_name = {ownTable} AND column_name = {ownColumn})
              AND NOT (table_schema = 'public' AND table_name = ANY({history}))
              AND EXISTS (SELECT 1 FROM information_schema.tables t
                          WHERE t.table_schema = c.table_schema AND t.table_name = c.table_name
                            AND t.table_type = 'BASE TABLE')
            ORDER BY 1, 2, 3
            """).ToListAsync(ct);

        var idArray = ids.ToArray();
        var patterns = ids.Select(Contains).ToArray();
        foreach (var c in columns)
        {
            var target = $"{Quoted(c.Schema)}.{Quoted(c.Table)}";
            var column = Quoted(c.Column);
            if (c.IsUuid)
            {
                var hit = await db.Database.SqlQueryRaw<Guid>(
                    $"SELECT {column} AS \"Value\" FROM {target} WHERE {column} = ANY({{0}}) LIMIT 1", (object)idArray)
                    .ToListAsync(ct);
                if (hit.Count > 0) return (hit[0], Address(c));
                continue;
            }
            if (!await MentionsAsync(target, column, patterns, ct)) continue;
            // Хоть один упомянут — уточняем, какой: путь отказа, его цена значения не имеет.
            foreach (var id in ids)
                if (await MentionsAsync(target, column, [Contains(id)], ct)) return (id, Address(c));
        }
        return null;

        static string Contains(Guid id) => "%" + id + "%";
        static string Address(HolderColumn c) => $"{c.Schema}.{c.Table}.{c.Column}";
    }

    private Task<bool> MentionsAsync(string target, string column, string[] patterns, CancellationToken ct) =>
        db.Database.SqlQueryRaw<bool>(
            $"SELECT EXISTS (SELECT 1 FROM {target} WHERE {column}::text ILIKE ANY({{0}})) AS \"Value\"",
            // string[] — сам по себе params object[]: без приведения массив разошёлся бы на параметры.
            (object)patterns)
            .SingleAsync(ct);

    private static string Quoted(string name) => '"' + name.Replace("\"", "\"\"") + '"';

    private sealed record HolderColumn(string Schema, string Table, string Column, bool IsUuid);
}
