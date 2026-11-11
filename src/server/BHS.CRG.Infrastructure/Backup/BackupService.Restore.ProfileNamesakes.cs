using BHS.CRG.Application.Backup;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Recognition;
using Microsoft.EntityFrameworkCore;

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
/// <para>Тёзка, правленный на этой системе, тоже заменяется — с предупреждением, что правка потеряна:
/// восстановление на тот же экземпляр уже перезаписывает правку по идентификатору, а перенос копии на
/// другую систему — тот же акт «привезённое главнее». Слить две редакции восстановление не умеет.</para>
/// </summary>
public partial class BackupService
{
    /// <summary>Собственная колонка профиля: в ней идентификатор — сам профиль, а не ссылка на него.</summary>
    private static readonly (string Table, string Column) ProfileIdColumn = ("recognition_profiles", "Id");

    private async Task ReplaceProfileNamesakesAsync(
        BackupRecognitionProfile[] items, List<string> warnings, CancellationToken ct)
    {
        // Тёзками считаются только профили, которые восстановление действительно положит: нетронутый
        // встроенный из копии оно пропускает (его переутверждает сидер), и здешний остаётся на месте.
        var restored = items
            .Where(i => i is not { IsBuiltIn: true, IsModified: false }
                        && Enum.TryParse<RecognitionProfileKind>(i.Kind, out _))
            .ToList();
        var restoredIds = restored.Select(i => i.Id).ToHashSet();
        var here = await db.RecognitionProfiles.AsNoTracking()
            .Where(p => p.Code != null)
            .Select(p => new { p.Id, p.Code, p.Name, p.IsModified })
            .ToListAsync(ct);

        foreach (var item in restored)
        {
            if (item.Code is null) continue;
            // Сравнение — как у уникального индекса, по точному коду: отказывал именно он.
            var namesake = here.FirstOrDefault(p => p.Code == item.Code && p.Id != item.Id);
            // Здешний профиль есть в копии под ДРУГИМ кодом — его строку восстановление обновит само.
            if (namesake is null || restoredIds.Contains(namesake.Id)) continue;

            var (from, to) = (namesake.Id.ToString(), item.Id.ToString());
            // GUID уникален — ложного совпадения при замене строки нет. Строки наборов, которые затем
            // приедут из копии с тем же идентификатором, всё равно перезапишутся.
            var rebound = await db.Database.ExecuteSqlAsync($"""
                UPDATE dataset_files
                SET "RecognitionProfiles" = replace("RecognitionProfiles"::text, {from}, {to})::jsonb,
                    "Grouping" = replace("Grouping"::text, {from}, {to})::jsonb
                WHERE "RecognitionProfiles"::text LIKE '%' || {from} || '%'
                   OR "Grouping"::text LIKE '%' || {from} || '%'
                """, ct);

            // Журнал действий держателем не считается: его запись неизменяема, а упоминание
            // идентификатора в тексте «было/стало» — история, а не ссылка, которая оборвётся.
            if (await HolderOfAsync(namesake.Id, ProfileIdColumn, skipActivityLog: true, ct) is { } holder)
                throw new ConflictException(ProfileOccupied(item, holder));

            await db.RecognitionProfiles.Where(p => p.Id == namesake.Id).ExecuteDeleteAsync(ct);
            warnings.Add(ProfileReplaced(namesake.Name, item.Code, namesake.IsModified, rebound));
        }
    }

    private static string ProfileReplaced(string name, string code, bool modifiedHere, int rebound) =>
        $"Профиль распознавания «{name}» этой системы заменён профилем из копии (код {code})" +
        (modifiedHere ? ": правка профиля на этой системе потеряна — в силе редакция из копии" : "") +
        (rebound > 0 ? $"; наборы данных этой системы ({rebound}) переведены на профиль из копии." : ".");

    private static string ProfileOccupied(BackupRecognitionProfile item, string holder) =>
        $"Профиль распознавания «{item.Name}» (код {item.Code}) есть и в копии, и на этой системе — под " +
        $"разными идентификаторами, и на здешний ссылается {holder}. Эту ссылку восстановление " +
        "перевести на профиль из копии не умеет, а заменить профиль без неё — значит оборвать её. " +
        "Восстановите копию на пустую систему.";
}
