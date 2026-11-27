using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Devices;

public sealed record DeviceCredentials(Guid DeviceId, Guid EmployeeId, string AccessToken,
    string RefreshToken, DateTimeOffset ExpiresAt);

public sealed record DeviceSessionResult(DeviceCredentials? Credentials, string? Reason)
{
    internal static DeviceSessionResult Refused(string reason) => new(null, reason);
    internal static DeviceSessionResult Issued(DeviceCredentials credentials) => new(credentials, null);
}

public sealed class DeviceSessions(WorkDbContext db, IModuleClock clock, IModuleEmployees employees,
    IDataProtectionProvider protection)
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(90);
    private static readonly TimeSpan AccessLifetime = TimeSpan.FromHours(12);
    private static readonly TimeSpan RetryWindow = TimeSpan.FromHours(8);
    private readonly IDataProtector _replay = protection.CreateProtector("BHS.CRG.Field.RefreshResponse.v1");

    internal async Task<(string Code, DateTimeOffset ExpiresAt)> InviteAsync(Guid employeeId, CancellationToken ct)
    {
        var code = NewSecret(18);
        var invitation = new DeviceInvitation
        {
            CodeHash = Hash(code), EmployeeId = employeeId, ExpiresAt = clock.UtcNow().AddHours(24),
        };
        db.DeviceInvitations.Add(invitation);
        await db.SaveChangesAsync(ct);
        return (code, invitation.ExpiresAt);
    }

    public async Task<DeviceSessionResult> JoinAsync(string code, CancellationToken ct)
    {
        var hash = Hash(code);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync($"work.device-invitation:{hash}", ct);
        var invitation = await db.DeviceInvitations.SingleOrDefaultAsync(i => i.CodeHash == hash, ct);
        if (invitation is null || invitation.RedeemedAt.HasValue || invitation.ExpiresAt <= clock.UtcNow())
            return DeviceSessionResult.Refused("invitation-invalid");

        var today = await clock.TodayAsync(ct);
        var employee = (await employees.GetAsync([invitation.EmployeeId], ct))?.SingleOrDefault();
        var now = clock.UtcNow();
        if (employee is not { Archived: false } || employee.NotWorkingOn(today) is not null || invitation.ExpiresAt <= now)
            return DeviceSessionResult.Refused("invitation-invalid");

        var session = new DeviceSession
        {
            InvitationId = invitation.Id, EmployeeId = invitation.EmployeeId,
            JoinedAt = now, SessionExpiresAt = now.Add(SessionLifetime), Grant = WorkModule.ReportOwn,
        };
        var credentials = Issue(session, now);
        invitation.RedeemedAt = now;
        db.DeviceSessions.Add(session);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return DeviceSessionResult.Issued(credentials);
    }

    public async Task<DeviceSessionResult> RefreshAsync(string token, CancellationToken ct)
    {
        var hash = Hash(token);
        var id = await db.DeviceSessions.AsNoTracking()
            .Where(s => s.RefreshHash == hash || s.PreviousRefreshHash == hash)
            .Select(s => (Guid?)s.Id).SingleOrDefaultAsync(ct);
        if (id is null) return DeviceSessionResult.Refused("invalid-session");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockSessionAsync(id.Value, ct);
        var session = await db.DeviceSessions.SingleAsync(s => s.Id == id, ct);
        if (session.RefreshHash != hash && session.PreviousRefreshHash != hash)
            return DeviceSessionResult.Refused("invalid-session");

        var now = clock.UtcNow();
        if (session.IsRevokedAt(now)) return DeviceSessionResult.Refused("device-revoked");
        if (session.SessionExpiresAt <= now) return DeviceSessionResult.Refused("invalid-session");

        if (session.PreviousRefreshHash == hash)
            return Replay(session, now);

        if (session.RevokedAt is null) session.SessionExpiresAt = now.Add(SessionLifetime);
        session.PreviousRefreshHash = session.RefreshHash;
        var credentials = Issue(session, now);
        session.RetryUntil = now.Add(RetryWindow);
        // Предыдущее значение даёт тот же ответ после потери связи. Открытые токены в базе не хранятся.
        session.ProtectedRotationResponse = _replay.Protect(JsonSerializer.Serialize(credentials));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return DeviceSessionResult.Issued(credentials);
    }

    internal async Task<DeviceSession?> FindAccessAsync(string token, CancellationToken ct)
    {
        var hash = Hash(token);
        return await db.DeviceSessions.AsNoTracking().SingleOrDefaultAsync(s => s.AccessHash == hash, ct);
    }

    internal Task LockSessionAsync(Guid id, CancellationToken ct) => LockAsync($"work.device-session:{id}", ct);

    private Task LockAsync(string key, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);

    private DeviceSessionResult Replay(DeviceSession session, DateTimeOffset now)
    {
        if (session.RetryUntil is not { } until || now >= until || session.ProtectedRotationResponse is not { } saved)
            return DeviceSessionResult.Refused("invalid-session");
        try
        {
            var credentials = JsonSerializer.Deserialize<DeviceCredentials>(_replay.Unprotect(saved));
            return credentials is null ? DeviceSessionResult.Refused("invalid-session") : DeviceSessionResult.Issued(credentials);
        }
        catch (CryptographicException)
        {
            return DeviceSessionResult.Refused("invalid-session");
        }
    }

    private static DeviceCredentials Issue(DeviceSession session, DateTimeOffset now)
    {
        var access = NewSecret(32);
        var refresh = NewSecret(32);
        var expires = new[] { now.Add(AccessLifetime), session.SessionExpiresAt,
            session.DeliveryUntil ?? DateTimeOffset.MaxValue }.Min();
        session.AccessHash = Hash(access);
        session.AccessExpiresAt = expires;
        session.RefreshHash = Hash(refresh);
        return new DeviceCredentials(session.Id, session.EmployeeId, access, refresh, expires);
    }

    private static string NewSecret(int bytes) => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(bytes));
    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
