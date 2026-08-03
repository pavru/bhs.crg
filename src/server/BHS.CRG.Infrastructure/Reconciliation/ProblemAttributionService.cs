using System.Text.Json;
using BHS.CRG.Application.Reconciliation;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Reconciliation;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Reconciliation;

/// <inheritdoc />
public class ProblemAttributionService(AppDbContext db) : IProblemAttribution
{
    /// <summary>Уровни, на которых сверка проявляется: (область, идентификатор).</summary>
    private sealed record Attribution(Guid DefinitionId, CatalogScope Scope, Guid ScopeId);

    /// <summary>
    /// Полный граф «сверка → уровни» за несколько пакетных запросов, независимо от числа прогонов и
    /// находок. Мемоизация на запрос: страница спрашивает атрибуцию несколько раз подряд.
    /// </summary>
    private List<Attribution>? _cache;

    private async Task<List<Attribution>> GraphAsync(CancellationToken ct)
    {
        if (_cache is not null) return _cache;

        var definitions = await db.Set<ReconciliationDefinition>().AsNoTracking()
            .Select(d => new { d.Id, Spec = d.Spec })
            .ToListAsync(ct);
        if (definitions.Count == 0) return _cache = [];

        // 1) Источники каждой спеки.
        var sourcesByDefinition = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var d in definitions)
        {
            var ids = SourceIdsOf(d.Spec);
            if (ids.Count > 0) sourcesByDefinition[d.Id] = ids;
        }
        var allSources = sourcesByDefinition.Values.SelectMany(x => x).Distinct().ToList();
        if (allSources.Count == 0) return _cache = [];

        // 2) Ось «география файла»: где лежит набор, которому принадлежит источник.
        var fileScopes = await db.DataSetSources.AsNoTracking()
            .Where(s => allSources.Contains(s.Id))
            .Select(s => new { s.Id, s.File!.Scope, s.File.ScopeId })
            .ToListAsync(ct);

        // 3) Ось «потребители»: объекты, привязанные к источнику, — документ реально ест эти строки.
        var bindings = await db.DataSetBindings.AsNoTracking()
            .Where(b => allSources.Contains(b.SourceId))
            .Select(b => new { b.SourceId, b.OwnerId })
            .ToListAsync(ct);

        var ownerIds = bindings.Select(b => b.OwnerId).Distinct().ToList();
        var ownerScopes = ownerIds.Count == 0
            ? []
            : await db.DomainObjects.AsNoTracking()
                .Where(o => ownerIds.Contains(o.Id))
                .Select(o => new { o.Id, o.ScopeLevel, o.ScopeId })
                .ToListAsync(ct);

        var scopeByOwner = ownerScopes.ToDictionary(o => o.Id, o => (o.ScopeLevel, o.ScopeId));
        var placesBySource = new Dictionary<Guid, HashSet<(CatalogScope, Guid)>>();

        void Place(Guid sourceId, CatalogScope scope, Guid? scopeId)
        {
            // System связи не даёт: иначе сверка над общесистемным файлом загорелась бы везде.
            if (scope == CatalogScope.System || scopeId is not { } id) return;
            if (!placesBySource.TryGetValue(sourceId, out var set))
                placesBySource[sourceId] = set = [];
            set.Add((scope, id));
        }

        foreach (var f in fileScopes) Place(f.Id, f.Scope, f.ScopeId);
        foreach (var b in bindings)
            if (scopeByOwner.TryGetValue(b.OwnerId, out var s)) Place(b.SourceId, s.ScopeLevel, s.ScopeId);

        // 4) Разворачиваем вверх: проблема комплекта видна и на его разделе, и на стройке.
        var setIds = placesBySource.Values.SelectMany(x => x)
            .Where(p => p.Item1 == CatalogScope.Set).Select(p => p.Item2).Distinct().ToList();
        var sectionIds = placesBySource.Values.SelectMany(x => x)
            .Where(p => p.Item1 == CatalogScope.Section).Select(p => p.Item2).Distinct().ToList();

        var setParents = await db.DocumentSets.AsNoTracking()
            .Where(s => setIds.Contains(s.Id))
            .Select(s => new { s.Id, s.SectionId })
            .ToListAsync(ct);
        var allSections = sectionIds.Concat(setParents.Select(s => s.SectionId)).Distinct().ToList();
        var sectionParents = await db.Sections.AsNoTracking()
            .Where(s => allSections.Contains(s.Id))
            .Select(s => new { s.Id, s.ConstructionId })
            .ToListAsync(ct);

        var sectionOfSet = setParents.ToDictionary(s => s.Id, s => s.SectionId);
        var constructionOfSection = sectionParents.ToDictionary(s => s.Id, s => s.ConstructionId);

        var result = new List<Attribution>();
        foreach (var (definitionId, sources) in sourcesByDefinition)
        {
            var places = new HashSet<(CatalogScope Scope, Guid Id)>();
            foreach (var sourceId in sources)
                if (placesBySource.TryGetValue(sourceId, out var p))
                    foreach (var x in p) places.Add(x);

            foreach (var (scope, id) in places.ToList())
            {
                if (scope == CatalogScope.Set && sectionOfSet.TryGetValue(id, out var sectionId))
                {
                    places.Add((CatalogScope.Section, sectionId));
                    if (constructionOfSection.TryGetValue(sectionId, out var c))
                        places.Add((CatalogScope.Construction, c));
                }
                else if (scope == CatalogScope.Section && constructionOfSection.TryGetValue(id, out var c2))
                {
                    places.Add((CatalogScope.Construction, c2));
                }
            }

            foreach (var (scope, id) in places) result.Add(new Attribution(definitionId, scope, id));
        }

        return _cache = result;
    }

    public async Task<IReadOnlyList<Guid>> ReconciliationIdsForAsync(
        CatalogScope scope, Guid scopeId, CancellationToken ct = default)
        => [.. (await GraphAsync(ct))
            .Where(a => a.Scope == scope && a.ScopeId == scopeId)
            .Select(a => a.DefinitionId)
            .Distinct()];

    public async Task<IReadOnlyList<Guid>> GlobalReconciliationsAsync(CancellationToken ct = default)
    {
        var attributed = (await GraphAsync(ct)).Select(a => a.DefinitionId).ToHashSet();
        var all = await db.Set<ReconciliationDefinition>().AsNoTracking().Select(d => d.Id).ToListAsync(ct);
        return [.. all.Where(id => !attributed.Contains(id))];
    }

    /// <summary>
    /// Источники спеки, включая свод по нескольким (#450). Спека — свободный jsonb, поэтому читаем
    /// терпимо: сломанная спека не должна ронять весь экран проблем.
    /// </summary>
    private static HashSet<Guid> SourceIdsOf(JsonDocument spec)
    {
        var ids = new HashSet<Guid>();
        if (spec.RootElement.ValueKind != JsonValueKind.Object) return ids;

        foreach (var sideName in (string[])["left", "right"])
        {
            if (!spec.RootElement.TryGetProperty(sideName, out var side)
                || side.ValueKind != JsonValueKind.Object) continue;

            Add(side);
            if (side.TryGetProperty("sources", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var part in list.EnumerateArray()) Add(part);
        }
        return ids;

        void Add(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object
                && e.TryGetProperty("sourceId", out var s)
                && s.ValueKind == JsonValueKind.String
                && Guid.TryParse(s.GetString(), out var id)
                && id != Guid.Empty)
                ids.Add(id);
        }
    }
}
