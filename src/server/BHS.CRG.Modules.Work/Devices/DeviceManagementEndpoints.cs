using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Access;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Devices;

internal sealed class DeviceManagementScope(IModuleUser user, IModuleEmployees employees, ManagerSites sites,
    WorkDbContext db, IModuleClock clock)
{
    public async Task EnsureAsync(Guid employeeId, CancellationToken ct)
    {
        if (await user.HasAsync(WorkModule.SitesAll, ct)) return;
        if (user.Id is not { } userId || await employees.OfAccountAsync(userId, ct) is not { } manager)
            throw new ForbiddenException(AssignmentGate.NotLinked);

        var managed = (await sites.OfEmployeeAsync(manager, ct: ct)).ToArray();
        var assignments = await db.Assignments.AsNoTracking()
            .Where(a => a.EmployeeId == employeeId && managed.Contains(a.ConstructionId)).ToListAsync(ct);
        foreach (var site in assignments.GroupBy(a => a.ConstructionId))
        {
            var today = await clock.TodayOnSiteAsync(site.Key, ct);
            if (today is { } day && site.Any(a => a.Covers(day))) return;
        }
        throw new ForbiddenException("Сотрудник не назначен на стройку в вашей области управления.");
    }
}

internal static class DeviceManagementEndpoints
{
    private sealed record InvitationRequest(string? ServerUrl, string? Transport, string? KeyFingerprint);
    private sealed record RevokeRequest(string? Reason);

    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/work/devices").WithTags("Устройства")
            .RequireAuthorization(AppPolicies.Permission(WorkModule.DevicesManage));
        group.MapPost("/employees/{employeeId:guid}/invitations", InviteAsync);
        group.MapGet("/employees/{employeeId:guid}", ListAsync);
        group.MapPost("/{id:guid}/revoke", RevokeAsync);
    }

    private static async Task<IResult> InviteAsync(Guid employeeId, InvitationRequest body, HttpResponse response,
        DeviceManagementScope scope, IModuleEmployees employees, IModuleClock clock, DeviceSessions sessions, CancellationToken ct)
    {
        await scope.EnsureAsync(employeeId, ct);
        var url = ValidateTransport(body);
        if (await employees.GetAsync([employeeId], ct) is not { } found)
            return AssignmentRules.DirectoryMissing();
        var employee = found.SingleOrDefault()
            ?? throw new NotFoundException("Сотрудник не найден.");
        if (employee.Archived || employee.NotWorkingOn(await clock.TodayAsync(ct)) is not null)
            throw new InvalidRequestException("Приглашение выдаётся действующему сотруднику.");

        var (invitationCode, expiresAt) = await sessions.InviteAsync(employeeId, ct);
        response.Headers.CacheControl = "no-store";
        return Results.Ok(new { serverUrl = url, transport = body.Transport, body.KeyFingerprint, invitationCode, expiresAt });
    }

    private static string ValidateTransport(InvitationRequest body)
    {
        if (body.Transport is not ("http" or "https-system" or "https-pinned"))
            throw new InvalidRequestException("Укажите режим связи из приглашения.");
        var scheme = body.Transport == "http" ? Uri.UriSchemeHttp : Uri.UriSchemeHttps;
        if (!Uri.TryCreate(body.ServerUrl, UriKind.Absolute, out var url) || url.Scheme != scheme ||
            string.IsNullOrEmpty(url.Host) || url.UserInfo.Length != 0 || url.AbsolutePath != "/" ||
            url.Query.Length != 0 || url.Fragment.Length != 0)
            throw new InvalidRequestException("Укажите адрес сервера с протоколом выбранного режима.");

        if (body.Transport == "https-pinned")
        {
            if (body.KeyFingerprint is not { } fingerprint ||
                !fingerprint.StartsWith("sha256/", StringComparison.Ordinal) ||
                !Convert.TryFromBase64String(fingerprint[7..], new byte[32], out var length) || length != 32)
                throw new InvalidRequestException("Укажите SHA-256 отпечаток открытого ключа сервера в формате sha256/base64.");
        }
        else if (body.KeyFingerprint is not null)
            throw new InvalidRequestException("Отпечаток используется в режиме https-pinned.");

        return url.GetLeftPart(UriPartial.Authority);
    }

    private static async Task<IResult> ListAsync(Guid employeeId, DeviceManagementScope scope, WorkDbContext db,
        CancellationToken ct)
    {
        await scope.EnsureAsync(employeeId, ct);
        var devices = await db.DeviceSessions.Where(s => s.EmployeeId == employeeId)
            .OrderByDescending(s => s.JoinedAt).ThenBy(s => s.Id).ToListAsync(ct);
        return Results.Ok(devices.Select(s => View(db, s)));
    }

    private static async Task<IResult> RevokeAsync(Guid id, RevokeRequest body, HttpRequest request, WorkDbContext db,
        DeviceSessions sessions, DeviceManagementScope scope, IModuleClock clock, IModuleSettings settings,
        IModuleUser user, CancellationToken ct)
    {
        var reason = body.Reason switch
        {
            "replacement" => DeviceRevocation.Replacement,
            "dismissal" => DeviceRevocation.Dismissal,
            "lost" => DeviceRevocation.Lost,
            _ => throw new InvalidRequestException("Укажите причину отзыва: замена телефона, увольнение или утеря."),
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await sessions.LockSessionAsync(id, ct);
        var session = await db.DeviceSessions.SingleOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new NotFoundException("Устройство не найдено.");
        await scope.EnsureAsync(session.EmployeeId, ct);
        if (!SeenVersion.TryRead(request, out var seen)) throw new InvalidRequestException(SeenVersion.Malformed);
        if (seen is null) throw new InvalidRequestException("Назовите версию устройства в If-Match.");
        if (seen != db.VersionOf(session)) throw new ConflictException("Устройство тем временем изменили.");
        if (session.RevokedAt is not null) throw new ConflictException("Устройство уже отозвано.");

        var days = reason == DeviceRevocation.Lost ? 0m : await settings.GetAsync(WorkSettings.DeviceDeliveryDays, ct);
        var today = await clock.TodayAsync(ct);
        var now = clock.UtcNow();
        session.RevokedOn = today;
        session.RevokedAt = now;
        session.RevokedBy = user.Id;
        session.Revocation = reason;
        session.DeliveryUntil = days > 0 ? now.AddDays((double)days) : null;
        if (session.DeliveryUntil is { } until && session.SessionExpiresAt > now)
            session.SessionExpiresAt = until;
        if (session.DeliveryUntil is null)
        {
            session.ProtectedRotationResponse = null;
            session.RetryUntil = null;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(View(db, session));
    }

    private static object View(WorkDbContext db, DeviceSession session) => new
    {
        deviceId = session.Id, session.EmployeeId, session.JoinedAt, expiresAt = session.SessionExpiresAt,
        session.RevokedAt, session.RevokedOn, session.RevokedBy, session.DeliveryUntil,
        reason = session.Revocation?.ToString().ToLowerInvariant(), version = db.VersionOf(session),
    };
}
