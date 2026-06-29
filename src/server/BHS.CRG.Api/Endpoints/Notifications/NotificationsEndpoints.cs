using System.Security.Claims;
using BHS.CRG.Application.Notifications;

namespace BHS.CRG.Api.Endpoints.Notifications;

public static class NotificationsEndpoints
{
    public static void MapNotificationsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/notifications").RequireAuthorization();

        // Список + счётчик: личные уведомления пользователя + общесистемные.
        g.MapGet("/", async (INotificationService svc, ClaimsPrincipal user, bool? unreadOnly, int? take, CancellationToken ct) =>
        {
            var uid = UserId(user);
            var items = await svc.GetAsync(uid, unreadOnly ?? false, take ?? 100, ct);
            var unread = await svc.UnreadCountAsync(uid, ct);
            return Results.Ok(new { items, unreadCount = unread });
        });

        // Текущее состояние системы и внешних компонент (общее для всех).
        g.MapGet("/health", (IHealthState health) => Results.Ok(health.Snapshot));

        g.MapPost("/{id:guid}/read", async (Guid id, INotificationService svc, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await svc.MarkReadAsync(id, UserId(user), ct);
            return Results.NoContent();
        });

        g.MapPost("/read-all", async (INotificationService svc, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await svc.MarkAllReadAsync(UserId(user), ct);
            return Results.NoContent();
        });

        g.MapDelete("/{id:guid}", async (Guid id, INotificationService svc, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await svc.DismissAsync(id, UserId(user), ct);
            return Results.NoContent();
        });

        g.MapDelete("/", async (INotificationService svc, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await svc.ClearAsync(UserId(user), ct);
            return Results.NoContent();
        });
    }

    private static Guid UserId(ClaimsPrincipal user)
        => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub")!);
}
