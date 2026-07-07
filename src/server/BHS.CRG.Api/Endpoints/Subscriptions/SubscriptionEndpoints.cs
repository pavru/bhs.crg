using BHS.CRG.Application.Subscriptions;
using BHS.CRG.Domain.Catalog;

namespace BHS.CRG.Api.Endpoints.Subscriptions;

/// <summary>
/// Управление подписчиками уровня стройка/раздел/комплект + резолв эффективных получателей
/// (прямые + унаследованные по иерархии). Этап 3 почты.
/// </summary>
public static class SubscriptionEndpoints
{
    public static void MapSubscriptionEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/subscriptions").RequireAuthorization();

        // Прямые подписчики уровня.
        g.MapGet("/", async (string scope, Guid scopeId, ISubscriptionService svc, CancellationToken ct) =>
            ParseScope(scope) is { } s
                ? Results.Ok(await svc.ListAsync(s, scopeId, ct))
                : Results.BadRequest(new { error = "Неверный scope." }));

        // Эффективные получатели (прямые + унаследованные) — для отправки.
        g.MapGet("/recipients", async (string scope, Guid scopeId, ISubscriptionService svc, CancellationToken ct) =>
            ParseScope(scope) is { } s
                ? Results.Ok(await svc.ResolveRecipientsAsync(s, scopeId, ct))
                : Results.BadRequest(new { error = "Неверный scope." }));

        g.MapPost("/", async (AddSubscriberRequest req, ISubscriptionService svc, CancellationToken ct) =>
        {
            if (ParseScope(req.Scope) is not { } s) return Results.BadRequest(new { error = "Неверный scope." });
            var dto = await svc.AddAsync(req.UserId, s, req.ScopeId, ct);
            return dto is null ? Results.NotFound(new { error = "Пользователь не найден." }) : Results.Ok(dto);
        });

        g.MapDelete("/{id:guid}", async (Guid id, ISubscriptionService svc, CancellationToken ct) =>
            await svc.RemoveAsync(id, ct) ? Results.NoContent() : Results.NotFound());
    }

    // Подписки только на уровни иерархии (не System).
    private static CatalogScope? ParseScope(string? scope) =>
        Enum.TryParse<CatalogScope>(scope, out var s) && s is CatalogScope.Construction or CatalogScope.Section or CatalogScope.Set
            ? s : null;

    private record AddSubscriberRequest(Guid UserId, string Scope, Guid ScopeId);
}
