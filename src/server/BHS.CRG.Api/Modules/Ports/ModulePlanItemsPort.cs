using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Ports;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// «Найди или создай» позиции перечня для модуля (ТЗ CORE-12, STG-6.1; change <c>plan-04-plan-item-port</c>).
///
/// <para><b>Конфликт решает база.</b> Ключи части пакета вставляются одним <c>INSERT … ON CONFLICT DO
/// NOTHING</c> по уникальному ключу позиции, потом читаются по ключу вторым запросом. «Прочитал — не
/// нашёл — вставил» здесь не годится: два импорта одной стройки прочитали бы пустоту оба и завели бы по
/// позиции. Чтение — отдельным запросом, а не в той же вставке: позицию, которую заводит соседний вызов,
/// вставка пропускает, дождавшись его, а снимок, с которым она начиналась, этой позиции не видит.</para>
///
/// <para>⚠️ <b>Ключи вставляются в ОДНОМ порядке</b> у всех вызовов. Вставка берёт блокировки ключей по
/// порядку строк, и два импорта одной стройки со встречным порядком — A держит k1 и ждёт k2, B держит k2 и
/// ждёт k1 — обрывались взаимной блокировкой (ревью PR #1596; ловит
/// <c>ModulePlanItemsPortTests.Встречные_пакеты_с_общими_ключами_не_обрывают_друг_друга</c>).</para>
///
/// <para><b>Пустой раздел — значение</b>: уникальный ключ построен с <c>NULLS NOT DISTINCT</c>
/// (<c>WorkPlanItemConfiguration</c>), и конфликт на «стройке в целом» ловится так же, как на разделе.
/// Чтение сравнивает раздел через <c>IS NOT DISTINCT FROM</c> по той же причине.</para>
///
/// <para>Каждая часть — своим запросом без общей транзакции: позиции заводятся до строк модуля, и
/// заведённое до сбоя найдёт повтор (ТЗ CORE-4, STG-6.1).</para>
///
/// <para>Уровень записи вида работы и единицы не сверяется со стройкой ключа: выбор вида работы
/// (<see cref="IModuleWorkTypes" />) отдаёт записи всех уровней, а уровень правами не управляет (issue #675).
/// Удаление уровня, чьи записи держат позиции другой стройки, отвергает <c>ScopeCascade</c>.</para>
/// </summary>
public sealed class ModulePlanItemsPort(AppDbContext db, IMediator mediator) : IModulePlanItems
{
    /// <summary>
    /// Сколько ключей идёт одним запросом. Деталь реализации, а не контракта: смета на тысячи строк
    /// иначе собрала бы один запрос с массивами на тысячи элементов.
    /// </summary>
    public const int ChunkSize = 500;

    /// <summary>Справочники ключа на область: строки отчёта зовут порт по одной, а типы на запросе не меняются.</summary>
    private (IReadOnlyDictionary<Guid, DocumentType>? WorkTypes, IReadOnlyDictionary<Guid, DocumentType>? Units)? families;

    public async Task<PlanItemOutcome> FindOrCreateAsync(PlanItemKey key, CancellationToken ct = default)
    {
        var batch = await FindOrCreateManyAsync([key], ct);
        return batch.Items.TryGetValue(key, out var item)
            ? PlanItemOutcome.Of(item)
            : PlanItemOutcome.Of(batch.Refusals.Single());
    }

    public async Task<PlanItemBatch> FindOrCreateManyAsync(
        IReadOnlyCollection<PlanItemKey> keys, CancellationToken ct = default)
    {
        var items = new Dictionary<PlanItemKey, PlanItemResult>();
        var refusals = new List<PlanItemRefusal>();
        var distinct = keys.Distinct().ToList();
        if (distinct.Count == 0) return new PlanItemBatch(items, refusals);

        refusals.AddRange(await RefusalsAsync(distinct, ct));
        var ordered = Without(distinct, refusals)
            .OrderBy(k => k.WorkTypeId).ThenBy(k => k.ConstructionId).ThenBy(k => k.SectionId).ThenBy(k => k.UnitId);
        foreach (var chunk in ordered.Chunk(ChunkSize))
            await FindOrCreateChunkAsync(chunk, items, refusals, ct);

        return new PlanItemBatch(items, refusals);
    }

    public async Task<IReadOnlyList<ModulePlanItem>> GetAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];
        var wanted = ids.Distinct().ToList();
        return [.. (await db.WorkPlanItems.AsNoTracking().Where(p => wanted.Contains(p.Id)).ToListAsync(ct))
            .Select(p => new ModulePlanItem(p.Id, KeyOf(p)))];
    }

    /// <summary>
    /// Часть пакета — вставкой и чтением, а то, что не дошло, — повторной проверкой ключа. Проверка до
    /// вставки и сама вставка разнесены во времени: раздел или стройку удаляют между ними, и тогда отказ
    /// внешнего ключа роняет вставку части целиком, а каскад их удаления уносит позицию между вставкой и
    /// чтением. Оба случая — не повод отвергнуть пакет (ревью PR #1596): ключ, которому составляющей
    /// теперь не хватает, становится отказом, остальные заводятся заново.
    /// </summary>
    private async Task FindOrCreateChunkAsync(PlanItemKey[] chunk,
        Dictionary<PlanItemKey, PlanItemResult> items, List<PlanItemRefusal> refusals, CancellationToken ct)
    {
        while (chunk.Length > 0)
        {
            Dictionary<PlanItemKey, PlanItemResult> found;
            try
            {
                found = await InsertAndReadAsync(chunk, ct);
            }
            catch (Exception e) when (IsForeignKeyViolation(e))
            {
                found = [];
            }

            foreach (var (key, item) in found) items[key] = item;
            var missing = chunk.Where(k => !found.ContainsKey(k)).ToList();
            if (missing.Count == 0) return;

            var late = await RefusalsAsync(missing, ct);
            // Ни одна составляющая не пропала, а позиции нет — объяснить нечем, и повтор ходил бы по кругу.
            if (late.Count == 0)
                throw new InvalidOperationException(
                    $"Позиция перечня с ключом {missing[0]} не заведена и не найдена, хотя ключ годен.");
            refusals.AddRange(late);
            chunk = [.. Without(missing, late)];
        }
    }

    private async Task<Dictionary<PlanItemKey, PlanItemResult>> InsertAndReadAsync(
        PlanItemKey[] chunk, CancellationToken ct)
    {
        var workTypes = chunk.Select(k => k.WorkTypeId).ToArray();
        var constructions = chunk.Select(k => k.ConstructionId).ToArray();
        var sections = chunk.Select(k => k.SectionId).ToArray();
        var units = chunk.Select(k => k.UnitId).ToArray();

        // ORDER BY по номеру строки — порядок блокировок задаёт звавший (см. описание класса), и вставка
        // обязана его не перемешать.
        var created = (await db.Database.SqlQuery<Guid>($"""
            INSERT INTO work_plan_items ("Id", "WorkTypeId", "ConstructionId", "SectionId", "UnitId", "CreatedAt", "UpdatedAt")
            SELECT gen_random_uuid(), k.w, k.c, k.s, k.u, now(), now()
            FROM unnest({workTypes}, {constructions}, {sections}, {units}) WITH ORDINALITY AS k(w, c, s, u, n)
            ORDER BY k.n
            ON CONFLICT ("WorkTypeId", "ConstructionId", "SectionId", "UnitId") DO NOTHING
            RETURNING "Id" AS "Value"
            """).ToListAsync(ct)).ToHashSet();

        var found = await db.WorkPlanItems.FromSql($"""
            SELECT p.* FROM work_plan_items p
            JOIN unnest({workTypes}, {constructions}, {sections}, {units}) AS k(w, c, s, u)
              ON p."WorkTypeId" = k.w AND p."ConstructionId" = k.c AND p."UnitId" = k.u
             AND p."SectionId" IS NOT DISTINCT FROM k.s
            """).AsNoTracking().ToListAsync(ct);

        return found.ToDictionary(KeyOf, p => new PlanItemResult(p.Id, created.Contains(p.Id)));
    }

    /// <summary>
    /// Отказы по ключам — по одному на ключ, первой негодной составляющей. Справочники и стройки
    /// читаются разом на весь пакет, а не по ключу: импорт на сотни строк ссылается на десятки записей.
    /// </summary>
    private async Task<List<PlanItemRefusal>> RefusalsAsync(List<PlanItemKey> keys, CancellationToken ct)
    {
        var (workFamily, unitFamily) = families ??= await FamiliesAsync(ct);
        var workTypes = await RecordsAsync(workFamily, keys.Select(k => k.WorkTypeId), ct);
        var units = await RecordsAsync(unitFamily, keys.Select(k => k.UnitId), ct);

        var constructionIds = keys.Select(k => k.ConstructionId).Distinct().ToList();
        var constructions = (await db.Constructions.AsNoTracking().Where(c => constructionIds.Contains(c.Id))
            .Select(c => c.Id).ToListAsync(ct)).ToHashSet();

        var sectionIds = keys.Select(k => k.SectionId).OfType<Guid>().Distinct().ToList();
        var sections = await db.Sections.AsNoTracking().Where(s => sectionIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => (s.ConstructionId, s.Name), ct);

        var refusals = new List<PlanItemRefusal>();
        foreach (var key in keys)
        {
            var refusal =
                RecordRefusal(PlanItemKeyPart.WorkType, key.WorkTypeId, workTypes,
                    "классификаторе видов работ", IModuleWorkTypes.NotSetUp)
                ?? ConstructionRefusal(key, constructions)
                ?? SectionRefusal(key, sections)
                ?? RecordRefusal(PlanItemKeyPart.Unit, key.UnitId, units,
                    "справочнике единиц измерения", IModuleUnits.NotSetUp);
            if (refusal is { } r) refusals.Add(new PlanItemRefusal(key, r.Part, r.Reason));
        }
        return refusals;
    }

    private async Task<(IReadOnlyDictionary<Guid, DocumentType>?, IReadOnlyDictionary<Guid, DocumentType>?)> FamiliesAsync(
        CancellationToken ct)
    {
        var types = await db.DocumentTypes.AsNoTracking().ToListAsync(ct);
        return (CoreDirectories.Family(types, CoreRecordTypes.WorkTypeCode),
            CoreDirectories.Family(types, CoreRecordTypes.UnitCode));
    }

    /// <summary>
    /// Записи справочника по идентификаторам — тем же запросом, что показывает уже стоящие ссылки: только
    /// записи своего справочника (тип и подтипы), архивные с признаком. <c>null</c> — справочник не заведён.
    /// </summary>
    private async Task<Dictionary<Guid, CommonDataRef>?> RecordsAsync(
        IReadOnlyDictionary<Guid, DocumentType>? family, IEnumerable<Guid> ids, CancellationToken ct) =>
        family is null
            ? null
            : (await mediator.Send(new CommonDataRefsByIdsQuery([.. family.Keys], [.. ids.Distinct()]), ct))
                .ToDictionary(r => r.Id);

    /// <summary>
    /// Вид работы и единица — запись СВОЕГО справочника и не в архиве (ТЗ CORE-34.4: новая ссылка на
    /// архивную запись — отказ). Своего: запись чужого типа под видом работы склеила бы позицию с тем, что
    /// видом работы не является, и ключ базы этого не поймал бы — он смотрит только на таблицу.
    /// </summary>
    private static (PlanItemKeyPart Part, string Reason)? RecordRefusal(
        PlanItemKeyPart part, Guid id, Dictionary<Guid, CommonDataRef>? records, string directory, string notSetUp)
    {
        if (records is null) return (part, notSetUp);
        // «Нет» — а не «удалили»: идентификатор мог прийти и чужой записи, и записи, которой не было вовсе.
        if (!records.TryGetValue(id, out var record)) return (part, $"В {directory} такой записи нет.");
        return record.Archived
            ? (part, $"«{record.DisplayName}» в архиве: новая позиция на неё не заводится.")
            : null;
    }

    private static (PlanItemKeyPart Part, string Reason)? ConstructionRefusal(
        PlanItemKey key, HashSet<Guid> constructions) =>
        constructions.Contains(key.ConstructionId) ? null : (PlanItemKeyPart.Construction, "Такой стройки нет.");

    private static (PlanItemKeyPart Part, string Reason)? SectionRefusal(
        PlanItemKey key, Dictionary<Guid, (Guid ConstructionId, string Name)> sections)
    {
        if (key.SectionId is not { } id) return null;
        if (!sections.TryGetValue(id, out var section)) return (PlanItemKeyPart.Section, "Такого раздела нет.");
        return section.ConstructionId == key.ConstructionId
            ? null
            : (PlanItemKeyPart.Section, $"Раздел «{section.Name}» принадлежит другой стройке.");
    }

    private static IEnumerable<PlanItemKey> Without(IEnumerable<PlanItemKey> keys, IEnumerable<PlanItemRefusal> refusals)
    {
        var refused = refusals.Select(r => r.Key).ToHashSet();
        return keys.Where(k => !refused.Contains(k));
    }

    private static bool IsForeignKeyViolation(Exception e) =>
        e is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation }
        || (e.InnerException is { } inner && IsForeignKeyViolation(inner));

    private static PlanItemKey KeyOf(WorkPlanItem p) => new(p.WorkTypeId, p.ConstructionId, p.SectionId, p.UnitId);
}
