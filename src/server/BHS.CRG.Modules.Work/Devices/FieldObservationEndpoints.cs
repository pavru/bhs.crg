using BHS.CRG.Modules.Work.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Devices;

internal static class FieldObservationEndpoints
{
    internal const string Path = "/device/observation";

    internal static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPut(Path, SaveAsync).RequireAuthorization(FieldReportEndpoints.DevicePolicy)
            .RequireRateLimiting("field-exchange");

    private sealed record ObservationRequest(Guid? DeviceId, int? ApkVersionCode,
        int? PendingReports, DateOnly? OldestPendingDate, DateTimeOffset? ObservedAt);

    private static async Task<IResult> SaveAsync(ObservationRequest body, HttpContext context,
        DeviceSessions sessions, WorkDbContext db, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        var deviceId = context.Features.Get<FieldDeviceAccess>()!.DeviceId;
        if (body.DeviceId is { } requested && requested != deviceId)
            return FieldReportEndpoints.Refusal(403, "device-mismatch");
        if (body.ApkVersionCode is not > 0 || body.PendingReports is not >= 0
            || (body.PendingReports == 0) != (body.OldestPendingDate is null) || body.ObservedAt is null
            || body.ObservedAt == DateTimeOffset.MinValue || body.ObservedAt == DateTimeOffset.MaxValue)
            return FieldReportEndpoints.Refusal(400, "invalid-request");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await sessions.LockSessionAsync(deviceId, ct);
        try
        {
            var (access, now) = await sessions.CheckDeliveryAsync(context.Request.Headers.Authorization.ToString()[7..],
                deviceId, ct);
            if (access.RevokedOn is not null) return FieldReportEndpoints.Refusal(403, "device-revoked");
            var observation = await db.DeviceObservations.SingleOrDefaultAsync(o => o.DeviceId == deviceId, ct);
            if (observation is null)
            {
                observation = new DeviceObservation { DeviceId = deviceId };
                db.DeviceObservations.Add(observation);
            }

            // Порядок приёма задаёт сервер: часы телефона могут быть переведены назад.
            observation.ApkVersionCode = body.ApkVersionCode.Value;
            observation.PendingReports = body.PendingReports.Value;
            observation.OldestPendingDate = body.OldestPendingDate;
            observation.ObservedAt = body.ObservedAt.Value.ToUniversalTime();
            var receivedAt = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
            observation.ReceivedAt = receivedAt;
            observation.LastContactAt = receivedAt;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { observation.ReceivedAt, observation.LastContactAt });
        }
        catch (FieldReportRefusal e)
        {
            return FieldReportEndpoints.Refusal(e.Status, e.Reason);
        }
    }
}
