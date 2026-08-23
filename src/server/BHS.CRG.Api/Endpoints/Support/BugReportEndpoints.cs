using System.Security.Claims;
using System.Text.Json;
using BHS.CRG.Application.Support;

namespace BHS.CRG.Api.Endpoints.Support;

public static class BugReportEndpoints
{
    public static void MapBugReportEndpoints(this IEndpointRouteBuilder app)
    {
        // Отправить может любой вошедший — на то и кнопка «Сообщить об ошибке» в боковой панели.
        // Читать и менять — только администратор: он тот самый фильтр между автором и публичным
        // репозиторием (issue #834).
        var user = app.MapGroup("/api/bug-reports").RequireAuthorization();
        var admin = app.MapGroup("/api/bug-reports").RequireAuthorization("Admin");

        user.MapPost("/", async (SubmitRequest req, IBugReportService svc, ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var id = await svc.SubmitAsync(UserId(principal), req.Message ?? "", req.Tech,
                req.ScreenshotBlobPath, ct);
            return Results.Ok(new { id });
        }).RequireRateLimiting("bug-report");

        admin.MapGet("/", async (IBugReportService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(ct)));

        admin.MapGet("/{id:guid}", async (Guid id, IBugReportService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetAsync(id, ct)));

        admin.MapPut("/{id:guid}/draft", async (Guid id, DraftRequest req, IBugReportService svc,
                CancellationToken ct) =>
            Results.Ok(await svc.SaveDraftAsync(id, req.Text, ct)));

        admin.MapPost("/{id:guid}/forward", async (Guid id, ForwardRequest req, IBugReportService svc,
                CancellationToken ct) =>
            Results.Ok(await svc.ForwardToGithubAsync(id, req.Title ?? "", ct)));

        admin.MapPost("/{id:guid}/fixed", async (Guid id, FixedRequest req, IBugReportService svc,
                CancellationToken ct) =>
            Results.Ok(await svc.MarkFixedAsync(id, req.Version ?? "", ct)));

        admin.MapPost("/{id:guid}/rejected", async (Guid id, IBugReportService svc, CancellationToken ct) =>
            Results.Ok(await svc.RejectAsync(id, ct)));

        admin.MapPost("/{id:guid}/reopen", async (Guid id, IBugReportService svc, CancellationToken ct) =>
            Results.Ok(await svc.ReopenAsync(id, ct)));
    }

    /// <param name="Tech">Техблок клиента: версия, экран, браузер, последние ошибки API, стек.</param>
    private record SubmitRequest(string? Message, JsonElement? Tech, string? ScreenshotBlobPath);
    private record DraftRequest(string? Text);
    private record ForwardRequest(string? Title);
    private record FixedRequest(string? Version);

    private static Guid UserId(ClaimsPrincipal user)
        => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub")!);
}
