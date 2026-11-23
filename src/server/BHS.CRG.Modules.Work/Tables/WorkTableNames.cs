using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Tables;

/// <summary>Названия ссылок, встречающихся в видимых строках до применения пользовательского отбора.</summary>
internal sealed class WorkTableNames(
    IModuleEmployees employees, IModuleConstructions constructions, IModuleWorkTypes workTypes, IModuleUnits units)
{
    internal async Task<IReadOnlyDictionary<Guid, string>> EmployeesAsync(IQueryable<Guid?> ids, CancellationToken ct)
    {
        var named = await IdsAsync(ids, ct);
        var people = named.Length == 0 ? [] : await employees.GetAsync(named, ct) ?? [];
        return people.ToDictionary(p => p.Id, p => p.FullName);
    }

    internal Task<IReadOnlyList<ModuleConstruction>> SitesAsync(CancellationToken ct) => constructions.ListAsync(ct);

    internal async Task<IReadOnlyDictionary<Guid, string>> WorkTypesAsync(IQueryable<Guid?> ids, CancellationToken ct)
    {
        var named = await IdsAsync(ids, ct);
        var types = named.Length == 0 ? [] : await workTypes.GetAsync(named, ct) ?? [];
        return types.ToDictionary(t => t.Id, t => t.DisplayName);
    }

    internal async Task<IReadOnlyDictionary<Guid, string>> VariantsAsync(IQueryable<Guid?> ids, CancellationToken ct)
    {
        var named = await IdsAsync(ids, ct);
        var variants = named.Length == 0 ? [] : await workTypes.GetVariantsAsync(named, ct) ?? [];
        return variants.ToDictionary(v => v.Id, v => v.Name);
    }

    internal async Task<IReadOnlyDictionary<Guid, string>> UnitsAsync(IQueryable<Guid?> ids, CancellationToken ct)
    {
        var named = await IdsAsync(ids, ct);
        var measures = named.Length == 0 ? [] : await units.GetAsync(named, ct) ?? [];
        return measures.ToDictionary(u => u.Id, u => u.Name);
    }

    private static Task<Guid[]> IdsAsync(IQueryable<Guid?> values, CancellationToken ct) =>
        values.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArrayAsync(ct);

    internal static string? Label<TKey>(IReadOnlyDictionary<TKey, string> labels, TKey? key) where TKey : struct =>
        key is { } id ? labels.GetValueOrDefault(id) : null;
}
