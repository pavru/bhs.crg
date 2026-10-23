using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

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
/// <para><b>Пустой раздел — значение</b>: уникальный ключ построен с <c>NULLS NOT DISTINCT</c>
/// (<c>WorkPlanItemConfiguration</c>), и конфликт на «стройке в целом» ловится так же, как на разделе.
/// Чтение сравнивает раздел через <c>IS NOT DISTINCT FROM</c> по той же причине.</para>
///
/// <para>Каждая часть — своим запросом без общей транзакции: позиции заводятся до строк модуля, и
/// заведённое до сбоя найдёт повтор (ТЗ CORE-4, STG-6.1).</para>
/// </summary>
public sealed class ModulePlanItemsPort(AppDbContext db) : IModulePlanItems
{
    /// <summary>
    /// Сколько ключей идёт одним запросом. Деталь реализации, а не контракта: смета на тысячи строк
    /// иначе собрала бы один запрос с массивами на тысячи элементов.
    /// </summary>
    public const int ChunkSize = 500;

    public async Task<PlanItemOutcome> FindOrCreateAsync(PlanItemKey key, CancellationToken ct = default)
    {
        var batch = await FindOrCreateManyAsync([key], ct);
        return batch.Items.TryGetValue(key, out var item)
            ? new PlanItemOutcome(item, null)
            : new PlanItemOutcome(null, batch.Refusals.Single());
    }

    public async Task<PlanItemBatch> FindOrCreateManyAsync(
        IReadOnlyCollection<PlanItemKey> keys, CancellationToken ct = default)
    {
        var distinct = keys.Distinct().ToList();
        var refusals = distinct.Count == 0 ? [] : await RefusalsAsync(distinct, ct);
        var refused = refusals.Select(r => r.Key).ToHashSet();

        var items = new Dictionary<PlanItemKey, PlanItemResult>();
        foreach (var chunk in distinct.Where(k => !refused.Contains(k)).Chunk(ChunkSize))
            foreach (var (key, item) in await FindOrCreateChunkAsync(chunk, ct))
                items[key] = item;

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
    /// Отказы по ключам — по одному на ключ, первой негодной составляющей. Справочники и стройки
    /// читаются разом на весь пакет, а не по ключу: импорт на сотни строк ссылается на десятки записей.
    /// </summary>
    private async Task<List<PlanItemRefusal>> RefusalsAsync(List<PlanItemKey> keys, CancellationToken ct)
    {
        var types = await db.DocumentTypes.AsNoTracking().ToListAsync(ct);
        var workTypes = CoreDirectories.Family(types, CoreRecordTypes.WorkTypeCode);
        var units = CoreDirectories.Family(types, CoreRecordTypes.UnitCode);

        var recordIds = keys.Select(k => k.WorkTypeId).Concat(keys.Select(k => k.UnitId)).Distinct().ToList();
        var records = await db.DomainObjects.AsNoTracking().Where(o => recordIds.Contains(o.Id))
            .Select(o => new Record(o.Id, o.CompositeTypeId, o.DisplayName, o.ArchivedAt != null))
            .ToDictionaryAsync(o => o.Id, ct);

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
                RecordRefusal(PlanItemKeyPart.WorkType, key.WorkTypeId, workTypes, records,
                    "вид работы", "классификатора видов работ", IModuleWorkTypes.NotSetUp)
                ?? ConstructionRefusal(key, constructions)
                ?? SectionRefusal(key, sections)
                ?? RecordRefusal(PlanItemKeyPart.Unit, key.UnitId, units, records,
                    "единица измерения", "справочника единиц измерения", IModuleUnits.NotSetUp);
            if (refusal is { } r) refusals.Add(new PlanItemRefusal(key, r.Part, r.Reason));
        }
        return refusals;
    }

    /// <summary>
    /// Вид работы и единица — существующая запись СВОЕГО справочника и не в архиве (ТЗ CORE-34.4: новая
    /// ссылка на архивную запись — отказ). Своего: запись чужого типа под видом работы склеила бы позицию
    /// с тем, что видом работы не является, и ключ базы этого не поймал бы — он смотрит только на таблицу.
    /// </summary>
    private static (PlanItemKeyPart Part, string Reason)? RecordRefusal(
        PlanItemKeyPart part, Guid id, IReadOnlyDictionary<Guid, DocumentType>? family,
        Dictionary<Guid, Record> records, string what, string directory, string notSetUp)
    {
        if (family is null) return (part, notSetUp);
        if (!records.TryGetValue(id, out var record)) return (part, $"Записи «{what}» нет — её удалили.");
        if (!family.ContainsKey(record.TypeId))
            return (part, $"«{record.Name}» — запись не из {directory}.");
        return record.Archived ? (part, $"«{record.Name}» в архиве: новая позиция на неё не заводится.") : null;
    }

    private static (PlanItemKeyPart Part, string Reason)? ConstructionRefusal(
        PlanItemKey key, HashSet<Guid> constructions) =>
        constructions.Contains(key.ConstructionId) ? null : (PlanItemKeyPart.Construction, "Стройки нет — её удалили.");

    private static (PlanItemKeyPart Part, string Reason)? SectionRefusal(
        PlanItemKey key, Dictionary<Guid, (Guid ConstructionId, string Name)> sections)
    {
        if (key.SectionId is not { } id) return null;
        if (!sections.TryGetValue(id, out var section)) return (PlanItemKeyPart.Section, "Раздела нет — его удалили.");
        return section.ConstructionId == key.ConstructionId
            ? null
            : (PlanItemKeyPart.Section, $"Раздел «{section.Name}» принадлежит другой стройке.");
    }

    private async Task<Dictionary<PlanItemKey, PlanItemResult>> FindOrCreateChunkAsync(
        PlanItemKey[] chunk, CancellationToken ct)
    {
        var workTypes = chunk.Select(k => k.WorkTypeId).ToArray();
        var constructions = chunk.Select(k => k.ConstructionId).ToArray();
        var sections = chunk.Select(k => k.SectionId).ToArray();
        var units = chunk.Select(k => k.UnitId).ToArray();

        var created = (await db.Database.SqlQuery<Guid>($"""
            INSERT INTO work_plan_items ("Id", "WorkTypeId", "ConstructionId", "SectionId", "UnitId", "CreatedAt", "UpdatedAt")
            SELECT gen_random_uuid(), k.w, k.c, k.s, k.u, now(), now()
            FROM unnest({workTypes}, {constructions}, {sections}, {units}) AS k(w, c, s, u)
            ON CONFLICT ("WorkTypeId", "ConstructionId", "SectionId", "UnitId") DO NOTHING
            RETURNING "Id" AS "Value"
            """).ToListAsync(ct)).ToHashSet();

        var found = await db.WorkPlanItems.FromSql($"""
            SELECT p.* FROM work_plan_items p
            JOIN unnest({workTypes}, {constructions}, {sections}, {units}) AS k(w, c, s, u)
              ON p."WorkTypeId" = k.w AND p."ConstructionId" = k.c AND p."UnitId" = k.u
             AND p."SectionId" IS NOT DISTINCT FROM k.s
            """).AsNoTracking().ToListAsync(ct);

        var items = found.ToDictionary(KeyOf, p => new PlanItemResult(p.Id, created.Contains(p.Id)));

        // Найдено меньше, чем вставлялось, — значит позицию удалили между вставкой и чтением: удаляет её
        // только каскад стройки или раздела. Молча вернуть неполный ответ значило бы, что модуль запишет
        // строки без позиции; отказ здесь — повод повторить импорт, который этот ключ уже не пропустит.
        if (chunk.Where(k => !items.ContainsKey(k)).Select(k => (PlanItemKey?)k).FirstOrDefault() is { } lost)
            throw new InvalidOperationException(
                $"Позиция перечня с ключом {lost} исчезла между вставкой и чтением: стройку или раздел удалили.");
        return items;
    }

    private static PlanItemKey KeyOf(WorkPlanItem p) => new(p.WorkTypeId, p.ConstructionId, p.SectionId, p.UnitId);

    private sealed record Record(Guid Id, Guid TypeId, string? Name, bool Archived);
}
