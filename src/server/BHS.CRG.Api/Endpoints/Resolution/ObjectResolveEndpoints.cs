using BHS.CRG.Application.Resolution;
using BHS.CRG.Domain.Catalog;
using MediatR;

namespace BHS.CRG.Api.Endpoints.Resolution;

/// <summary>
/// Батч-резолв «строка→объект» (issue #183, Фаза 3). POST только ради тела-батча — эндпоинт
/// идемпотентный и read-only by contract: находит существующие объекты каталога, ничего не мутирует.
/// </summary>
public static class ObjectResolveEndpoints
{
    public static void MapObjectResolveEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/objects/resolve-batch", async (ResolveBatchReq req, IMediator m) =>
        {
            if (!Enum.TryParse<CatalogScope>(req.Scope, ignoreCase: true, out var scope))
                return Results.BadRequest($"Неизвестный scope: {req.Scope}");

            var items = (req.Items ?? [])
                .Select(i => new ObjectResolveItem(
                    i.TypeId,
                    Enum.TryParse<ObjectMatchStrategy>(i.Strategy, ignoreCase: true, out var st) ? st : ObjectMatchStrategy.Name,
                    i.Value, i.FieldKey, i.Fields))
                .ToList();

            var res = await m.Send(new ResolveObjectsBatchQuery(scope, req.ScopeId, items));
            return Results.Ok(res.Select(r => r is null
                ? null
                : new { entryId = r.EntryId, displayName = r.DisplayName, scope = r.Scope.ToString() }));
        }).RequireAuthorization();
    }

    private record ResolveBatchReq(string Scope, Guid? ScopeId, List<ResolveItemReq>? Items);
    private record ResolveItemReq(Guid TypeId, string Strategy, string? Value, string? FieldKey,
        Dictionary<string, string?>? Fields);
}
