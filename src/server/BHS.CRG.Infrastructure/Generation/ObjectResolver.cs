using System.Text.Json;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Resolution;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Infrastructure.Common;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Generation;

/// <summary>
/// Реализация единого резолвера «строка→объект» (issue #183). Read-only: только запрос к
/// <see cref="AppDbContext.DomainObjects"/> (AsNoTracking), без каких-либо write-путей.
/// Кэши живут в пределах scoped-времени жизни (один запрос/генерация): скоп-цепочки, типы, индексы.
/// </summary>
public sealed class ObjectResolver(AppDbContext db) : IObjectResolver
{
    /// <summary>Разделитель компонентов составного ключа — Unit Separator (U+001F): не встречается
    /// в пользовательских данных, экранирование не нужно. Ключ внутренний, человекочитаемость не важна.</summary>
    private const char CompositeSeparator = '\u001F';

    private readonly Dictionary<(CatalogScope, Guid?), ScopeChain> _chains = [];
    private readonly Dictionary<(Guid, CatalogScope, Guid?), TypeIndex> _indexes = [];
    private List<DocumentType>? _allTypes;

    public async Task<Guid?> ResolveAsync(ObjectMatchRequest req, CatalogScope scopeLevel, Guid? scopeId, CancellationToken ct = default)
    {
        var index = await GetIndexAsync(req.TypeId, scopeLevel, scopeId, ct);
        return index.Match(req);
    }

    public async Task<IReadOnlyList<Guid?>> ResolveManyAsync(
        IReadOnlyList<ObjectMatchRequest> reqs, CatalogScope scopeLevel, Guid? scopeId, CancellationToken ct = default)
    {
        var result = new List<Guid?>(reqs.Count);
        foreach (var req in reqs)
            result.Add(await ResolveAsync(req, scopeLevel, scopeId, ct));
        return result;
    }

    private async Task<ScopeChain> GetChainAsync(CatalogScope scopeLevel, Guid? scopeId, CancellationToken ct)
    {
        var key = (scopeLevel, scopeId);
        if (_chains.TryGetValue(key, out var cached)) return cached;
        var chain = await ScopeChains.LoadForScopeAsync(db, scopeLevel, scopeId, ct);
        _chains[key] = chain;
        return chain;
    }

    private async Task<List<DocumentType>> AllTypesAsync(CancellationToken ct) =>
        _allTypes ??= await db.DocumentTypes.AsNoTracking().ToListAsync(ct);

    private async Task<TypeIndex> GetIndexAsync(Guid typeId, CatalogScope scopeLevel, Guid? scopeId, CancellationToken ct)
    {
        var cacheKey = (typeId, scopeLevel, scopeId);
        if (_indexes.TryGetValue(cacheKey, out var cached)) return cached;

        var chain = await GetChainAsync(scopeLevel, scopeId, ct);
        var allTypes = await AllTypesAsync(ct);
        var typeIds = DescendantTypeIds(typeId, allTypes); // сам тип + подтипы (paste-совместимо)

        // Кандидаты — только объекты общих данных (Facet==null) нужных типов в скоп-поддереве;
        // scope-фильтр в SQL, приоритет узкого scope — в памяти (first-wins при построении индексов).
        var candidates = await db.DomainObjects.AsNoTracking()
            .Where(o => o.Facet == null && typeIds.Contains(o.CompositeTypeId) &&
                ((o.ScopeLevel == CatalogScope.Set && o.ScopeId == chain.SetId) ||
                 (o.ScopeLevel == CatalogScope.Section && o.ScopeId == chain.SectionId) ||
                 (o.ScopeLevel == CatalogScope.Construction && o.ScopeId == chain.ConstructionId) ||
                 o.ScopeLevel == CatalogScope.System))
            .ToListAsync(ct);
        candidates = candidates.OrderBy(o => (int)o.ScopeLevel).ToList(); // Set=1 … System=5

        var index = TypeIndex.Build(candidates, allTypes);
        _indexes[cacheKey] = index;
        return index;
    }

    /// <summary>Тип + все его потомки по цепочке ParentId.</summary>
    private static HashSet<Guid> DescendantTypeIds(Guid rootId, List<DocumentType> all)
    {
        var result = new HashSet<Guid> { rootId };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var t in all)
                if (t.ParentId is { } p && result.Contains(p) && result.Add(t.Id)) grew = true;
        }
        return result;
    }

    /// <summary>identity-поля типа в ПОРЯДКЕ СХЕМЫ (наследование-aware, ближний тип первым).</summary>
    private static IReadOnlyList<string> IdentityFieldKeys(DocumentType type, IReadOnlyList<DocumentType> all) =>
        SchemaTags.TaggedFields(type, all)
            .Where(t => t.Tag == FunctionalTag.Identity)
            .Select(t => t.Key)
            .ToList();

    /// <summary>Составной ключ из значений полей (порядок = identityKeys). Пустой компонент → null
    /// (строгий режим: не индексируем/не ищем частичный ключ — иначе ложные слияния).</summary>
    private static string? BuildCompositeKey(IReadOnlyList<string> identityKeys, Func<string, string?> read)
    {
        if (identityKeys.Count == 0) return null;
        var parts = new string[identityKeys.Count];
        for (var i = 0; i < identityKeys.Count; i++)
        {
            var norm = MatchKeyNormalizer.Normalize(read(identityKeys[i]));
            if (norm.Length == 0) return null;
            parts[i] = norm;
        }
        return string.Join(CompositeSeparator, parts);
    }

    private static string? ReadField(JsonDocument data, string field)
    {
        if (!data.RootElement.TryGetProperty(field, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    /// <summary>Индекс кандидатов одного (тип, scope): упорядоченный список + lookup по имени/составному ключу.</summary>
    private sealed class TypeIndex
    {
        private readonly List<DomainObject> _candidates;
        private readonly Dictionary<string, Guid> _byName;
        private readonly Dictionary<string, Guid> _byIdentity;
        private readonly IReadOnlyList<DocumentType> _allTypes;
        private readonly Dictionary<Guid, IReadOnlyList<string>> _identityKeysByType;

        private TypeIndex(List<DomainObject> candidates, Dictionary<string, Guid> byName,
            Dictionary<string, Guid> byIdentity, IReadOnlyList<DocumentType> allTypes,
            Dictionary<Guid, IReadOnlyList<string>> identityKeysByType)
        {
            _candidates = candidates;
            _byName = byName;
            _byIdentity = byIdentity;
            _allTypes = allTypes;
            _identityKeysByType = identityKeysByType;
        }

        public static TypeIndex Build(List<DomainObject> candidates, IReadOnlyList<DocumentType> allTypes)
        {
            var byName = new Dictionary<string, Guid>();
            var byIdentity = new Dictionary<string, Guid>();
            var identityKeysByType = new Dictionary<Guid, IReadOnlyList<string>>();

            foreach (var c in candidates) // уже в порядке scope-приоритета → TryAdd = узкий побеждает
            {
                var name = MatchKeyNormalizer.Normalize(c.DisplayName);
                if (name.Length > 0) byName.TryAdd(name, c.Id);
                foreach (var a in c.Aliases)
                {
                    var an = MatchKeyNormalizer.Normalize(a);
                    if (an.Length > 0) byName.TryAdd(an, c.Id);
                }

                if (!identityKeysByType.TryGetValue(c.CompositeTypeId, out var idKeys))
                {
                    var type = allTypes.FirstOrDefault(t => t.Id == c.CompositeTypeId);
                    idKeys = type is null ? [] : IdentityFieldKeys(type, allTypes);
                    identityKeysByType[c.CompositeTypeId] = idKeys;
                }
                var ck = BuildCompositeKey(idKeys, f => ReadField(c.Data, f));
                if (ck is not null) byIdentity.TryAdd(ck, c.Id);
            }

            // Предвычисленные identity-ключи по типу переиспользуем и для lookup-стороны (IdentityKey).
            return new TypeIndex(candidates, byName, byIdentity, allTypes, identityKeysByType);
        }

        public Guid? Match(ObjectMatchRequest req)
        {
            switch (req.Strategy)
            {
                case ObjectMatchStrategy.Field:
                {
                    if (req.FieldKey is null) return null;
                    var needle = MatchKeyNormalizer.Normalize(req.Value);
                    if (needle.Length == 0) return null;
                    foreach (var c in _candidates) // scope-приоритетный порядок → первый = узкий
                    {
                        var hay = ReadField(c.Data, req.FieldKey);
                        if (hay is not null && MatchKeyNormalizer.Normalize(hay) == needle) return c.Id;
                    }
                    return null;
                }
                case ObjectMatchStrategy.Name:
                {
                    var n = MatchKeyNormalizer.Normalize(req.Value);
                    return n.Length > 0 && _byName.TryGetValue(n, out var g) ? g : null;
                }
                case ObjectMatchStrategy.IdentityKey:
                {
                    if (req.Fields is null) return null;
                    var idKeys = IdentityKeysForType(req.TypeId);
                    var ck = BuildCompositeKey(idKeys, f => req.Fields.TryGetValue(f, out var v) ? v : null);
                    return ck is not null && _byIdentity.TryGetValue(ck, out var g) ? g : null;
                }
                default:
                    return null;
            }
        }

        private IReadOnlyList<string> IdentityKeysForType(Guid typeId)
        {
            if (_identityKeysByType.TryGetValue(typeId, out var cached)) return cached;
            var type = _allTypes.FirstOrDefault(t => t.Id == typeId);
            var keys = type is null ? [] : IdentityFieldKeys(type, _allTypes);
            _identityKeysByType[typeId] = keys;
            return keys;
        }
    }
}
