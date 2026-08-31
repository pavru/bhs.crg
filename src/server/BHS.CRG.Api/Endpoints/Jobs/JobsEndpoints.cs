using System.Security.Claims;
using BHS.CRG.Application.Jobs;

namespace BHS.CRG.Api.Endpoints.Jobs;

public static class JobsEndpoints
{
    public static void MapJobsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/jobs").RequireAuthorization();

        // Активные (Queued/Running) фоновые задачи текущего пользователя — источник данных индикатора.
        g.MapGet("/active", async (IJobService svc, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok(await svc.GetActiveForUserAsync(UserId(user), ct)));

        // Задача по id — в любом статусе, включая завершившуюся (issue #898). 404 — нет такой или
        // чужая. Список активных отвечает на «что идёт сейчас», этот — на «чем кончилось»: у экрана
        // ответ приносит уведомление, а у того, кто запустил задачу снаружи, уведомлений нет.
        g.MapGet("/{id:guid}", async (Guid id, IJobService svc, ClaimsPrincipal user, CancellationToken ct) =>
            await svc.GetAsync(id, UserId(user), ct) is { } job ? Results.Ok(job) : Results.NotFound());

        // Отмена задачи из очереди (только Queued; выполняемые добегают). 409 — уже выполняется/завершена.
        g.MapPost("/{id:guid}/cancel", async (Guid id, IJobService svc, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var cancelled = await svc.CancelAsync(id, UserId(user), ct);
            return cancelled
                ? Results.NoContent()
                : Results.Conflict(new { error = "Задачу нельзя отменить — она уже выполняется или завершена." });
        });
    }

    private static Guid UserId(ClaimsPrincipal user)
        => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub")!);
}
