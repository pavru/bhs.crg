using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;

using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Обратный опрос потерянных ссылок для модуля (ТЗ CORE-34.2, issue #1184): объявления модуля
/// превращаются в колонки и таблицы ядра, искать — дело скана (<see cref="ModuleLostReferenceScan" />).
///
/// <para>Живёт в корне композиции по той же причине, что <see cref="ModuleRecordHolders" />: только он
/// видит и базу, и модули сборки.</para>
/// </summary>
public sealed class ModuleReferenceTargetsPort(ModuleRegistry registry, ModuleLostReferenceScan scan)
    : IModuleReferenceTargets
{
    /// <summary>
    /// Где живут записи каждого вида цели. ⚠️ ОДНА таблица соответствия на оба вопроса порта: разойдись
    /// они — пометка в форме и счётчик говорили бы разное об одной ссылке. Полноту стережёт
    /// <c>ModuleReferenceInventoryTests</c>: у каждого значения <see cref="ReferenceTarget" /> есть строка.
    /// </summary>
    public static IReadOnlyDictionary<ReferenceTarget, Type> Entities { get; } = new Dictionary<ReferenceTarget, Type>
    {
        [ReferenceTarget.Record] = typeof(DomainObject),
        [ReferenceTarget.Construction] = typeof(Construction),
        [ReferenceTarget.Section] = typeof(Section),
        [ReferenceTarget.DocumentSet] = typeof(DocumentSet),
        [ReferenceTarget.DocumentType] = typeof(DocumentType),
        [ReferenceTarget.WorkPlanItem] = typeof(WorkPlanItem),
        [ReferenceTarget.User] = typeof(ApplicationUser),
    };

    public async Task<IReadOnlyDictionary<Guid, ReferenceState>> StatesAsync(
        ReferenceTarget target, IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        // Архив бывает только у записи справочника (issue #1185), и читается он ТЕМ ЖЕ запросом, что
        // и существование: состояние спрашивают на каждое открытие и каждую правку счёта.
        if (target == ReferenceTarget.Record)
        {
            var records = await scan.RecordStatesAsync(ids, ct);
            return ids.Distinct().ToDictionary(id => id, id =>
                !records.TryGetValue(id, out var archived) ? ReferenceState.Lost
                : archived ? ReferenceState.Archived
                : ReferenceState.Present);
        }

        var present = await scan.ExistingAsync(TableOf(target), ids, ct);
        return ids.Distinct().ToDictionary(id => id, id => present.Contains(id) ? ReferenceState.Present : ReferenceState.Lost);
    }

    /// <summary>Таблица вида цели. Архив — только у записи справочника: колонку называет это место,
    /// и скан спрашивает о ней тем же проходом, что и о существовании.</summary>
    private CoreTable TableOf(ReferenceTarget target) =>
        scan.TableOf(Entities[target], target == ReferenceTarget.Record ? nameof(DomainObject.ArchivedAt) : null);

    public async Task<ReferenceFindings> NotPresentAsync(
        string moduleCode, bool includeArchived, CancellationToken ct = default)
    {
        var module = registry.Find(moduleCode)
            ?? throw new InvalidOperationException($"Модуля «{moduleCode}» в этой сборке нет.");
        if (module.Schema?.Name is not { } schema)
            return new ReferenceFindings([], [], DateTimeOffset.MinValue);

        // Помнящие колонки не опрашиваются: их цель удаляют законно (учётная запись автора счёта).
        var declared = module.References.Where(r => r.Holds).ToList();
        var columns = new Dictionary<ReferencingColumn, ModuleReference>(ReferenceEqualityComparer.Instance);
        foreach (var reference in declared)
            columns[new ReferencingColumn(reference.Table, reference.Column,
                reference.Target is { } target ? TableOf(target) : null, reference.Document?.Via)] = reference;

        var found = await scan.FindAsync(schema, [.. columns.Keys], includeArchived, ct);

        return new ReferenceFindings(
            [.. found.Lost.Select(hit =>
            {
                var reference = columns[hit.Column];
                return new ReferenceFinding(reference.Table, reference.Column, reference.Target!.Value, hit.TargetId,
                    hit.Archived ? ReferenceState.Archived : ReferenceState.Lost,
                    hit.DocumentKey, hit.Rows, reference.Document?.Table);
            })],
            [.. found.Unscanned.Select(skipped =>
            {
                var reference = columns[skipped.Column];
                return new UncheckedColumn(reference.Table, reference.Column, skipped.Reason switch
                {
                    UnscannedReason.NoTarget => UncheckedReason.MixedTargets,
                    UnscannedReason.Unreadable => UncheckedReason.Unreadable,
                    _ => UncheckedReason.MissingInSchema,
                }, reference.What);
            })],
            found.AsOf);
    }
}
