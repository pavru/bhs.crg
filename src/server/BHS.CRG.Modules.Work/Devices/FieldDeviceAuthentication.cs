using System.Security.Claims;
using System.Text.Encodings.Web;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BHS.CRG.Modules.Work.Devices;

internal sealed record FieldDeviceAccess(Guid DeviceId, Guid EmployeeId, string Grant,
    DateTimeOffset AccessExpiresAt, DateOnly? RevokedOn, DateTimeOffset? DeliveryUntil)
{
    // Дату и автора существующего отчёта берёт вызывающая операция Work из сохранённой записи.
    public bool AllowsReport(Guid authorId, DateOnly reportDate, bool delivering, DateTimeOffset now) =>
        Grant == WorkModule.ReportOwn && authorId == EmployeeId && now < AccessExpiresAt &&
        (RevokedOn is null || (delivering && now < DeliveryUntil && reportDate <= RevokedOn));

    public bool AllowsPhoto(Guid authorId, DateOnly reportDate, bool declaredForReport, DateTimeOffset now) =>
        (RevokedOn is null || declaredForReport) && AllowsReport(authorId, reportDate, delivering: true, now);
}

internal sealed class FieldDeviceAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, DeviceSessions sessions, IModuleClock clock)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "FieldDevice";
    private string _refusal = "invalid-session";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var token = header[7..];
        if (token.Length != 43) return AuthenticateResult.Fail(_refusal);

        var session = await sessions.FindAccessAsync(token, Context.RequestAborted);
        var now = clock.UtcNow();
        if (session is null) return AuthenticateResult.Fail(_refusal);
        if (session.IsRevokedAt(now))
        {
            _refusal = "device-revoked";
            return AuthenticateResult.Fail(_refusal);
        }
        if (session.AccessExpiresAt <= now || session.SessionExpiresAt <= now || session.Grant != WorkModule.ReportOwn)
            return AuthenticateResult.Fail(_refusal);

        Context.Features.Set(new FieldDeviceAccess(session.Id, session.EmployeeId, session.Grant,
            session.AccessExpiresAt, session.RevokedOn, session.DeliveryUntil));
        var identity = new ClaimsIdentity([new Claim("field_device_id", session.Id.ToString()),
            new Claim("field_employee_id", session.EmployeeId.ToString()),
            new Claim("field_grant", session.Grant)], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = _refusal == "device-revoked" ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized;
        Response.Headers.CacheControl = "no-store";
        return Response.WriteAsJsonAsync(new { reason = _refusal }, Context.RequestAborted);
    }
}
