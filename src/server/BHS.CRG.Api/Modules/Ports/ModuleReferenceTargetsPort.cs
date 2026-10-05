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
        var present = await scan.ExistingAsync(scan.TableOf(Entities[target]), ids, ct);
        return ids.Distinct().ToDictionary(id => id, id => present.Contains(id) ? ReferenceState.Present : ReferenceState.Lost);
    }

    public async Task<LostReferences> LostAsync(string moduleCode, CancellationToken ct = default)
    {
        var module = registry.Find(moduleCode)
            ?? throw new InvalidOperationException($"Модуля «{moduleCode}» в этой сборке нет.");
        if (module.Schema?.Name is not { } schema)
            return new LostReferences([], [], DateTimeOffset.MinValue);

        // Помнящие колонки не опрашиваются: их цель удаляют законно (учётная запись автора счёта).
        var declared = module.References.Where(r => r.Holds).ToList();
        var columns = new Dictionary<ReferencingColumn, ModuleReference>(ReferenceEqualityComparer.Instance);
        foreach (var reference in declared)
            columns[new ReferencingColumn(reference.Table, reference.Column,
                reference.Target is { } target ? scan.TableOf(Entities[target]) : null, reference.Document?.Via)] = reference;

        var found = await scan.FindAsync(schema, [.. columns.Keys], ct);

        return new LostReferences(
            [.. found.Lost.Select(hit =>
            {
                var reference = columns[hit.Column];
                return new LostReference(reference.Table, reference.Column, reference.Target!.Value, hit.TargetId, hit.DocumentKey, hit.Rows, reference.Document?.Table);
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
