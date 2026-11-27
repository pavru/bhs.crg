using System.Text.Json;
using BHS.CRG.Modules.Work.Devices;

namespace BHS.CRG.Api.Endpoints.Field;

internal static class FieldSessionEndpoints
{
    private sealed record JoinRequest(string? InvitationCode);
    private sealed record RefreshRequest(string? RefreshToken);

    internal static void Map(RouteGroupBuilder field)
    {
        field.MapPost("/join", async (HttpContext context, DeviceSessions sessions, CancellationToken ct) =>
        {
            var body = await ReadAsync<JoinRequest>(context, ct);
            return Valid(body?.InvitationCode)
                ? Reply(await sessions.JoinAsync(body!.InvitationCode!, ct))
                : InvalidRequest();
        }).AllowAnonymous().RequireRateLimiting("field-join");
        field.MapPost("/refresh", async (HttpContext context, DeviceSessions sessions, CancellationToken ct) =>
        {
            var body = await ReadAsync<RefreshRequest>(context, ct);
            return Valid(body?.RefreshToken)
                ? Reply(await sessions.RefreshAsync(body!.RefreshToken!, ct))
                : InvalidRequest();
        }).AllowAnonymous().RequireRateLimiting("field-refresh");
    }

    private static bool Valid(string? secret) =>
        secret is { Length: <= 256 } && !string.IsNullOrWhiteSpace(secret);

    private static async Task<T?> ReadAsync<T>(HttpContext context, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.HasJsonContentType()) return default;
        try
        {
            return await context.Request.ReadFromJsonAsync<T>(ct);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static IResult InvalidRequest() => Results.BadRequest(new { reason = "invalid-request" });

    private static IResult Reply(DeviceSessionResult result)
    {
        if (result.Credentials is { } credentials) return Results.Ok(credentials);
        var status = result.Reason == "invalid-session" ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden;
        return Results.Json(new { reason = result.Reason }, statusCode: status);
    }
}
