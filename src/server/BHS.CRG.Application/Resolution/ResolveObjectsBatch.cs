using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Objects;
using MediatR;

namespace BHS.CRG.Application.Resolution;

/// <summary>Один элемент батч-резолва «строка→объект».</summary>
public record ObjectResolveItem(
    Guid TypeId, ObjectMatchStrategy Strategy,
    string? Value = null, string? FieldKey = null, IReadOnlyDictionary<string, string?>? Fields = null);

/// <summary>Результат резолва одного элемента (для UI: подставить ссылку с именем/скопом).</summary>
public record ObjectResolveResult(Guid EntryId, string? DisplayName, CatalogScope Scope);

/// <summary>
/// Батч-резолв «строка→объект» (issue #183, Фаза 3): находит СУЩЕСТВУЮЩИЕ объекты каталога для
/// набора строк (paste, будущие вызовы). Read-only: ничего не создаёт. Порядок результата =
/// порядку <paramref name="Items"/>; элемент null — совпадения нет.
/// </summary>
public record ResolveObjectsBatchQuery(CatalogScope Scope, Guid? ScopeId, IReadOnlyList<ObjectResolveItem> Items)
    : IRequest<IReadOnlyList<ObjectResolveResult?>>;

public class ResolveObjectsBatchHandler(IObjectResolver resolver, IRepository<DomainObject> repo)
    : IRequestHandler<ResolveObjectsBatchQuery, IReadOnlyList<ObjectResolveResult?>>
{
    public async Task<IReadOnlyList<ObjectResolveResult?>> Handle(ResolveObjectsBatchQuery q, CancellationToken ct)
    {
        if (q.Items.Count == 0) return [];

        var reqs = q.Items
            .Select(i => new ObjectMatchRequest
            {
                TypeId = i.TypeId, Strategy = i.Strategy, Value = i.Value, FieldKey = i.FieldKey, Fields = i.Fields,
            })
            .ToList();

        var ids = await resolver.ResolveManyAsync(reqs, q.Scope, q.ScopeId, ct);

        // Догружаем имя/скоп совпавших объектов одним запросом (для подстановки ссылки в UI).
        var matched = ids.Where(x => x is not null).Select(x => x!.Value).Distinct().ToList();
        var byId = matched.Count == 0
            ? []
            : (await repo.FindAsync(o => matched.Contains(o.Id), ct))
                .ToDictionary(o => o.Id, o => new ObjectResolveResult(o.Id, o.DisplayName, o.ScopeLevel));

        return ids.Select(id => id is { } g && byId.TryGetValue(g, out var r) ? r : null).ToList();
    }
}
