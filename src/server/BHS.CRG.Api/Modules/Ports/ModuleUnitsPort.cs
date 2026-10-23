using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules.Ports;
using MediatR;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Справочник единиц измерения для модуля (ТЗ PLAN-6.1) — узкими запросами ядра, без данных записи:
/// у единицы модулю нужно только название.
/// </summary>
public sealed class ModuleUnitsPort(IMediator mediator, IRepository<DocumentType> types) : IModuleUnits
{
    public async Task<IReadOnlyList<ModuleUnit>?> GetAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (await FamilyAsync(ct) is not { } family) return null;
        if (ids.Count == 0) return [];
        return Units(await mediator.Send(new CommonDataRefsByIdsQuery(family, ids), ct));
    }

    public async Task<ModuleChoice<ModuleUnit>?> SearchAsync(string? query, int limit, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (await FamilyAsync(ct) is not { } family) return null;
        var found = await mediator.Send(new SearchCommonDataForChoiceQuery(family, query, limit), ct);
        return new ModuleChoice<ModuleUnit>(Units(found.Items), found.InArchive);
    }

    private async Task<IReadOnlyCollection<Guid>?> FamilyAsync(CancellationToken ct) =>
        CoreDirectories.Family(await types.GetAllAsync(ct), CoreRecordTypes.UnitCode)?.Keys.ToList();

    // Порядок задаёт порт: в базе сортировка своя (ею отсекается предел), а список читает человек.
    private static IReadOnlyList<ModuleUnit> Units(IEnumerable<CommonDataRef> refs) =>
        [.. refs.Select(r => new ModuleUnit(r.Id, r.DisplayName ?? "", r.Archived))
            .OrderBy(u => u.Name, StringComparer.CurrentCulture)];
}
