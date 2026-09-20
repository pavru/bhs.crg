using BHS.CRG.Api.Auth;
using BHS.CRG.Modules;
using System.Security.Claims;
using System.Text.Json;
using BHS.CRG.Application.Support;

namespace BHS.CRG.Api.Endpoints.Support;

public static class BugReportEndpoints
{
    public static void MapBugReportEndpoints(this IEndpointRouteBuilder app)
    {
        // Отправить может любой вошедший — на то и кнопка «Сообщить об ошибке» в боковой панели.
        // Права здесь нет намеренно: оно досталось бы каждой роли и никогда ни у кого не снималось,
        // то есть галка в редакторе ролей, которая ничего не разграничивает (issue #947).
        var user = app.MapGroup("/api/bug-reports").RequireAuthorization();

        // Читать и разбирать ЧУЖИЕ обращения — под правом: разбирающий видит сообщения всех
        // пользователей вместе со снимками их экранов и решает, что уйдёт в публичный репозиторий
        // (issue #834). Раньше здесь стояло имя роли «Admin» — то же самое, но незаметное для
        // редактора ролей и неизменяемое без правки кода.
        var review = app.MapGroup("/api/bug-reports")
            .RequireAuthorization(AppPolicies.Permission(CorePermissions.SupportReview));

        user.MapPost("/", async (SubmitRequest req, IBugReportService svc, ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var id = await svc.SubmitAsync(UserId(principal), req.Message ?? "", req.Tech,
                req.ScreenshotBlobPath, ct);
            return Results.Ok(new { id });
        }).RequireRateLimiting("bug-report");

        review.MapGet("/", async (IBugReportService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(ct)));

        review.MapGet("/{id:guid}", async (Guid id, IBugReportService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetAsync(id, ct)));

        review.MapPut("/{id:guid}/draft", async (Guid id, DraftRequest req, IBugReportService svc,
                CancellationToken ct) =>
            Results.Ok(await svc.SaveDraftAsync(id, req.Text, ct)));

        review.MapPost("/{id:guid}/forward", async (Guid id, ForwardRequest req, IBugReportService svc,
                CancellationToken ct) =>
            Results.Ok(await svc.ForwardToGithubAsync(id, req.Title ?? "", req.Body, ct)));

        review.MapPost("/{id:guid}/fixed", async (Guid id, FixedRequest req, IBugReportService svc,
                CancellationToken ct) =>
            Results.Ok(await svc.MarkFixedAsync(id, req.Version ?? "", ct)));

        review.MapPost("/{id:guid}/rejected", async (Guid id, IBugReportService svc, CancellationToken ct) =>
            Results.Ok(await svc.RejectAsync(id, ct)));

        review.MapPost("/{id:guid}/reopen", async (Guid id, IBugReportService svc, CancellationToken ct) =>
            Results.Ok(await svc.ReopenAsync(id, ct)));
    }

    /// <param name="Tech">Техблок клиента: версия, экран, браузер, последние ошибки API, стек.</param>
    private record SubmitRequest(string? Message, JsonElement? Tech, string? ScreenshotBlobPath);
    private record DraftRequest(string? Text);
    /// <param name="Body">Текст с экрана: уходит именно он, даже если правку ещё не сохранили.</param>
    private record ForwardRequest(string? Title, string? Body);
    private record FixedRequest(string? Version);

    private static Guid UserId(ClaimsPrincipal user)
        => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub")!);
}
