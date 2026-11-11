using System.Text.RegularExpressions;
using BHS.CRG.Application.Backup;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Recognition;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace BHS.CRG.Infrastructure.Backup;

/// <summary>
/// Тёзки встроенных профилей распознавания при восстановлении — часть <see cref="BackupService" />
/// (issue #1570).
///
/// <para>Профили восстанавливаются по идентификатору. Но встроенные (<c>invoice</c>, <c>spec-table</c>…)
/// система заводит сама, при первом старте, под СВОИМ случайным идентификатором. Копия системы, где
/// встроенный профиль правили, несёт тот же код под идентификатором исходной — для восстановления это
/// новый профиль, вставка, и уникальный индекс по коду (<c>IX_recognition_profiles_Code</c>) отказывал.
/// Откатывалось всё восстановление: достаточно было один раз поправить «Счёт на оплату», чтобы копия
/// перестала переезжать на новый сервер. Тот же класс беды у типов закрыт #1400
/// (<c>ReplaceTypeNamesakesAsync</c>).</para>
///
/// <para><b>Правило.</b> Здешний тёзка уступает место профилю из копии, и профиль ложится под
/// идентификатором КОПИИ: копия — то, что переносят, и все ссылки внутри неё (наборы, группы листов,
/// данные модулей, о которых ядро не знает) остаются верными сами. Ссылки ЭТОЙ системы на тёзку
/// переживают замену: известные держатели ядра — карта профилей набора и группы листов — переписываются
/// на идентификатор из копии. Остался держатель, которого восстановление переписать не умеет, или порт
/// модулей не ответил — отказ, назвав профиль и держателя: пропущенная ссылка тихо оборвалась бы.</para>
///
/// <para><b>Нетронутый встроенный из копии</b> восстановление не кладёт (его редакцию держит сидер), но
/// идентификатор и у него берётся из копии: здешний профиль с тем же кодом остаётся со своим содержимым и
/// принимает идентификатор копии. Иначе ссылки наборов из копии — карта профилей, профиль группы листов —
/// вели бы в пустоту, и группа без табличного тэга теряла бы таблицу (ревью PR #1896, решение владельца
/// 11.11.2026). Ссылки этой системы переписываются и проверяются точно так же, как у заменяемого.</para>
///
/// <para>Тёзка, правленный на этой системе, тоже заменяется — с предупреждением, что правка потеряна:
/// восстановление на тот же экземпляр уже перезаписывает правку по идентификатору, а перенос копии на
/// другую систему — тот же акт «привезённое главнее». Слить две редакции восстановление не умеет.</para>
///
/// <para>Все тёзки обрабатываются разом: одна выборка наборов, один проход по схеме, одно удаление —
/// копия с полудюжиной правленных встроенных не должна обходить базу полдюжины раз внутри транзакции
/// восстановления (ревью PR #1896).</para>
/// </summary>
public partial class BackupService
{
    /// <summary>Собственная колонка профиля: в ней идентификатор — сам профиль, а не ссылка на него.</summary>
    private static readonly (string Table, string Column) ProfileIdColumn = ("recognition_profiles", "Id");

    /// <summary>
    /// Таблицы, где идентификатор профиля — история, а не ссылка: журнал действий (запись неизменяема,
    /// «было/стало» — прошлое), задачи (параметры и ход отработавшего распознавания), уведомления и
    /// сообщения об ошибках (текст для человека). Ничто не разрешает эти упоминания в профиль, и
    /// оборваться им нечему — а отказ из-за них останавливал бы переезд на систему, где уже работали.
    /// </summary>
    private static readonly string[] ProfileHistoryTables = ["activity_log", "jobs", "notifications", "bug_reports"];

    private async Task ReplaceProfileNamesakesAsync(
        BackupRecognitionProfile[] items, BackupDataSetFile[] copyFiles, List<string> warnings, CancellationToken ct)
    {
        // Тёзками считаются только профили, которые восстановление действительно положит: нетронутый
        // встроенный из копии оно пропускает (его переутверждает сидер), и здешний остаётся на месте.
        var known = items.Where(i => Enum.TryParse<RecognitionProfileKind>(i.Kind, out _)).ToList();
        var restored = known.Where(i => i is not { IsBuiltIn: true, IsModified: false }).ToList();
        var untouched = known.Where(i => i is { IsBuiltIn: true, IsModified: false }).ToList();
        var restoredCodes = restored.ToDictionary(i => i.Id, i => i.Code);
        var copyIds = items.Select(i => i.Id).ToHashSet();
        var here = await db.RecognitionProfiles.AsNoTracking()
            .Where(p => p.Code != null)
            .Select(p => new HereProfile(p.Id, p.Code!, p.Name, p.IsModified))
            .ToListAsync(ct);

        await ReleaseMovingCodesAsync(here, restoredCodes, ct);

        // Сравнение — как у уникального индекса, по точному коду: отказывал именно он. Здешний
        // профиль, который есть в копии сам, — не тёзка: его строку восстановление обновит.
        var replaced = NamesakesOf(restored, here, p => !restoredCodes.ContainsKey(p.Id));
        var rekeyed = NamesakesOf(untouched, here, p => !copyIds.Contains(p.Id));
        List<(BackupRecognitionProfile Item, HereProfile Here)> all = [.. replaced, .. rekeyed];
        if (all.Count == 0) return;

        var map = all.ToDictionary(n => n.Here.Id, n => n.Item.Id);
        var rebound = await RebindDataSetFilesAsync(map, copyFiles.Select(f => f.Id).ToHashSet(), ct);

        await RefuseHeldProfilesAsync(all, ct);

        var replacedIds = replaced.Select(n => n.Here.Id).ToArray();
        await db.RecognitionProfiles.Where(p => replacedIds.Contains(p.Id)).ExecuteDeleteAsync(ct);
        foreach (var (item, namesake) in replaced)
            warnings.Add(ProfileReplaced(namesake, item.Code!, rebound.GetValueOrDefault(namesake.Id)));
        // Содержимое остаётся здешним, меняется только ключ: внешних ключей на профиль нет, ссылки — JSON.
        foreach (var (item, namesake) in rekeyed)
            await db.Database.ExecuteSqlAsync(
                $"""UPDATE recognition_profiles SET "Id" = {item.Id} WHERE "Id" = {namesake.Id}""", ct);
    }

    private static List<(BackupRecognitionProfile Item, HereProfile Here)> NamesakesOf(
        List<BackupRecognitionProfile> copy, List<HereProfile> here, Func<HereProfile, bool> eligible) =>
        copy
            .Select(item => (Item: item, Here: here.FirstOrDefault(
                p => p.Code == item.Code && p.Id != item.Id && eligible(p))))
            .Where(n => n.Item.Code is not null && n.Here is not null)
            .Select(n => (n.Item, Here: n.Here!))
            .ToList();

    /// <summary>
    /// Код здешнего профиля, который копия уносит под другим кодом, снимается заранее (ревью PR #1896).
    ///
    /// <para>Иначе обмен кодами — профиль копии получает код здешнего, здешний уходит под другой —
    /// ляжет одним сохранением, вставкой и обновлением. Строка, помеченная изменённой без прежних
    /// значений, не говорит EF, что код освобождается, и порядок команд не гарантирован: вставка могла
    /// уйти первой и упасть на том же индексе. Код вернёт само восстановление — из копии.</para>
    /// </summary>
    private async Task ReleaseMovingCodesAsync(
        List<HereProfile> here, Dictionary<Guid, string?> restoredCodes, CancellationToken ct)
    {
        var moving = here
            .Where(p => restoredCodes.TryGetValue(p.Id, out var code) && code != p.Code)
            .Select(p => p.Id)
            .ToArray();
        if (moving.Length > 0)
            await db.RecognitionProfiles.Where(p => moving.Contains(p.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Code, (string?)null), ct);
    }

    /// <summary>
    /// Карта профилей и группы листов наборов этой системы — на идентификатор из копии; ответ — сколько
    /// наборов ЭТОЙ системы переведено, по каждому тёзке.
    ///
    /// <para>Замена — в памяти, без учёта регистра: GUID уникален, ложного совпадения нет, а записанный
    /// заглавными — та же ссылка. Набор, который затем приедет из копии с тем же идентификатором,
    /// переписывается тоже (иначе проход по схеме принял бы его за держателя), но в счёт не идёт: его
    /// строку восстановление заменит копией, и называть его переведённым было бы неправдой.</para>
    /// </summary>
    private async Task<Dictionary<Guid, int>> RebindDataSetFilesAsync(
        Dictionary<Guid, Guid> map, HashSet<Guid> copyFileIds, CancellationToken ct)
    {
        var patterns = map.Keys.Select(id => "%" + id + "%").ToArray();
        var rows = await db.Database.SqlQueryRaw<FileProfileRefs>("""
            SELECT "Id", "RecognitionProfiles"::text AS "RecognitionProfiles", "Grouping"::text AS "Grouping"
            FROM dataset_files
            WHERE "RecognitionProfiles"::text ILIKE ANY({0}) OR "Grouping"::text ILIKE ANY({0})
            """, (object)patterns).ToListAsync(ct);

        var anyNamesake = new Regex(
            string.Join("|", map.Keys.Select(id => Regex.Escape(id.ToString()))), RegexOptions.IgnoreCase);
        var rebound = new Dictionary<Guid, int>();
        foreach (var row in rows)
        {
            var hit = new HashSet<Guid>();
            string? Rebind(string? json) => json is null ? null : anyNamesake.Replace(json, m =>
            {
                var from = Guid.Parse(m.Value);
                hit.Add(from);
                return map[from].ToString();
            });
            await db.Database.ExecuteSqlRawAsync(
                """UPDATE dataset_files SET "RecognitionProfiles" = @profiles, "Grouping" = @grouping WHERE "Id" = @id""",
                [Jsonb("profiles", Rebind(row.RecognitionProfiles)), Jsonb("grouping", Rebind(row.Grouping)),
                 new NpgsqlParameter("id", row.Id)], ct);
            if (copyFileIds.Contains(row.Id)) continue;
            foreach (var from in hit) rebound[from] = rebound.GetValueOrDefault(from) + 1;
        }
        return rebound;

        static NpgsqlParameter Jsonb(string name, string? value) =>
            new(name, NpgsqlDbType.Jsonb) { Value = (object?)value ?? DBNull.Value };
    }

    /// <summary>
    /// Отказ, если на тёзку ссылается то, что восстановление не переписало: данные модулей (порт —
    /// сначала, как у всякого удаления записи ядра; не ответил — тоже отказ: «проверить не удалось» не
    /// значит «никто не держит») или любая колонка базы, кроме таблиц истории.
    /// </summary>
    private async Task RefuseHeldProfilesAsync(
        List<(BackupRecognitionProfile Item, HereProfile Here)> namesakes, CancellationToken ct)
    {
        var hereIds = namesakes.Select(n => n.Here.Id).ToArray();
        var modules = await holders.FindEachAsync(hereIds, documents: false, ct);
        if (modules.Unverified is { } unverified)
            throw new ConflictException(ProfileOccupied(namesakes[0].Item, "проверить не удалось: " + unverified.Lines[0]));
        if (namesakes.FirstOrDefault(n => modules.ByRecord.ContainsKey(n.Here.Id)) is { Item: not null } heldByModule)
            throw new ConflictException(ProfileOccupied(heldByModule.Item, modules.ByRecord[heldByModule.Here.Id].Lines[0]));

        if (await CoreHolderOfAsync(hereIds, ProfileIdColumn, ProfileHistoryTables, ct) is { } core)
            throw new ConflictException(ProfileOccupied(namesakes.First(n => n.Here.Id == core.Id).Item, core.Address));
    }

    private static string ProfileReplaced(HereProfile namesake, string code, int rebound) =>
        $"Профиль распознавания «{namesake.Name}» этой системы заменён профилем из копии (код {code})" +
        (namesake.IsModified ? ": правка профиля на этой системе потеряна — в силе редакция из копии" : "") +
        (rebound > 0 ? $"; наборы данных этой системы ({rebound}) переведены на профиль из копии." : ".");

    private static string ProfileOccupied(BackupRecognitionProfile item, string holder) =>
        $"Профиль распознавания «{item.Name}» (код {item.Code}) есть и в копии, и на этой системе — под " +
        $"разными идентификаторами, и на здешний ссылается {holder}. Эту ссылку восстановление " +
        "перевести на идентификатор из копии не умеет, а без неё она оборвалась бы. " +
        "Восстановите копию на пустую систему.";

    private sealed record HereProfile(Guid Id, string Code, string Name, bool IsModified);

    private sealed record FileProfileRefs(Guid Id, string? RecognitionProfiles, string? Grouping);
}
