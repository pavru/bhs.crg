using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Devices;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static BHS.CRG.Tests.Integration.AssignmentWorld;

namespace BHS.CRG.Tests.Integration;

[Collection(AssignmentHost.Collection)]
public sealed class FieldDeviceSessionTests(AssignmentHost host)
{
    private readonly AssignmentWorld _world = new(host);

    [Fact]
    public async Task Invitation_connects_one_device_for_an_employee_without_an_account()
    {
        var (employee, manager, _) = await SetupAsync(scoped: true);
        using var office = manager;
        using var phone = host.CreateClient();
        var invitation = await InviteAsync(office, employee);
        var requests = await Task.WhenAll(JoinAsync(phone, invitation), JoinAsync(phone, invitation));
        try
        {
            var credentials = await CredentialsAsync(Assert.Single(requests, r => r.IsSuccessStatusCode));
            await ReasonAsync(Assert.Single(requests, r => !r.IsSuccessStatusCode),
                HttpStatusCode.Forbidden, "invitation-invalid");
            Assert.Equal(employee, credentials.EmployeeId);
            Assert.True(credentials.ExpiresAt > AssignmentHost.Now);

            var (authenticated, access) = await AuthenticateAsync(credentials.AccessToken);
            Assert.True(authenticated.Succeeded);
            Assert.Equal(FieldDeviceAuthentication.SchemeName, authenticated.Principal!.Identity!.AuthenticationType);
            Assert.Equal(WorkModule.ReportOwn, access!.Grant);
            Assert.True(access.AllowsReport(employee, Day("2026-10-31"), delivering: false, AssignmentHost.Now));
            Assert.False(access.AllowsReport(Guid.NewGuid(), Day("2026-10-31"), delivering: true, AssignmentHost.Now));
            phone.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
            using var work = await phone.GetAsync("/api/work/reports");
            using var core = await phone.GetAsync("/api/account/access");
            Assert.Equal(HttpStatusCode.Unauthorized, work.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, core.StatusCode);

            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
            Assert.Single(await db.DeviceSessions.Where(s => s.EmployeeId == employee).ToListAsync());
        }
        finally
        {
            foreach (var request in requests) request.Dispose();
        }
    }

    [Fact]
    public async Task Invitation_expiry_and_office_scope_are_enforced()
    {
        var (employee, manager, directory) = await SetupAsync(scoped: true);
        using var office = manager;
        using var phone = host.CreateClient();
        var foreign = await _world.EmployeeAsync(directory, "Сотрудник без общей стройки");
        using var outOfScope = await office.PostAsJsonAsync(InvitationPath(foreign), InvitationBody);
        Assert.Equal(HttpStatusCode.Forbidden, outOfScope.StatusCode);
        var (_, installer) = await _world.UserAsync("Монтажник", WorkModule.ReportOwn);
        using (installer)
        using (var forbidden = await installer.PostAsJsonAsync(InvitationPath(employee), InvitationBody))
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var code = await InviteAsync(office, employee);
        try
        {
            host.ModuleTime = AssignmentHost.Now.AddHours(24);
            using var expired = await JoinAsync(phone, code);
            await ReasonAsync(expired, HttpStatusCode.Forbidden, "invitation-invalid");
        }
        finally
        {
            host.ModuleTime = null;
        }
    }

    [Fact]
    public async Task Refresh_replays_the_same_rotation_only_within_its_window()
    {
        var (employee, manager, _) = await SetupAsync();
        using var office = manager;
        using var phone = host.CreateClient();
        var initial = await ConnectAsync(office, phone, employee);
        using var rotated = await RefreshAsync(phone, initial.RefreshToken);
        var next = await CredentialsAsync(rotated);
        Assert.Equal(initial.DeviceId, next.DeviceId);
        Assert.NotEqual(initial.RefreshToken, next.RefreshToken);
        try
        {
            host.ModuleTime = AssignmentHost.Now.AddHours(6);
            using var retry = await RefreshAsync(phone, initial.RefreshToken);
            Assert.Equal(next, await CredentialsAsync(retry));
            Assert.True((await AuthenticateAsync(next.AccessToken)).Result.Succeeded);
            host.ModuleTime = AssignmentHost.Now.AddHours(8);
            using var lateReplay = await RefreshAsync(phone, initial.RefreshToken);
            await ReasonAsync(lateReplay, HttpStatusCode.Unauthorized, "invalid-session");
            host.ModuleTime = AssignmentHost.Now.AddDays(90);
            using var expired = await RefreshAsync(phone, next.RefreshToken);
            await ReasonAsync(expired, HttpStatusCode.Unauthorized, "invalid-session");
        }
        finally
        {
            host.ModuleTime = null;
        }
    }

    [Theory]
    [InlineData("replacement")]
    [InlineData("dismissal")]
    [InlineData("lost")]
    public async Task Revocation_limits_each_device_operation_and_keeps_a_secret_free_record(string reason)
    {
        var (employee, manager, _) = await SetupAsync();
        using var office = manager;
        using var phone = host.CreateClient();
        var initial = await ConnectAsync(office, phone, employee);
        using var rotated = await RefreshAsync(phone, initial.RefreshToken);
        var next = await CredentialsAsync(rotated);
        var version = await VersionAsync(office, employee);
        using var missingReason = await RevokeAsync(office, initial.DeviceId, version, null);
        Assert.Equal(HttpStatusCode.BadRequest, missingReason.StatusCode);
        using var missingVersion = await RevokeAsync(office, initial.DeviceId, null, reason);
        Assert.Equal(HttpStatusCode.BadRequest, missingVersion.StatusCode);
        using var stale = await RevokeAsync(office, initial.DeviceId, "0", reason);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var attempts = await Task.WhenAll(RevokeAsync(office, initial.DeviceId, version, reason),
            RevokeAsync(office, initial.DeviceId, version, reason));
        try
        {
            var applied = Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.Conflict);
            var record = await JsonAsync(applied);
            Assert.Equal(reason, record.GetProperty("reason").GetString());
            Assert.Equal(AssignmentHost.Now, record.GetProperty("revokedAt").GetDateTimeOffset());
            Assert.DoesNotContain("Token", record.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (var attempt in attempts) attempt.Dispose();
        }

        using var previous = await RefreshAsync(phone, initial.RefreshToken);
        if (reason == "lost")
        {
            using var current = await RefreshAsync(phone, next.RefreshToken);
            await ReasonAsync(previous, HttpStatusCode.Forbidden, "device-revoked");
            await ReasonAsync(current, HttpStatusCode.Forbidden, "device-revoked");
            Assert.False((await AuthenticateAsync(next.AccessToken)).Result.Succeeded);
            return;
        }

        Assert.Equal(next, await CredentialsAsync(previous));
        var (authenticated, access) = await AuthenticateAsync(next.AccessToken);
        Assert.True(authenticated.Succeeded);
        Assert.NotNull(access);
        var day = Day("2026-10-31");
        Assert.True(access.AllowsReport(employee, day, delivering: true, AssignmentHost.Now));
        Assert.False(access.AllowsReport(employee, day, delivering: false, AssignmentHost.Now));
        Assert.False(access.AllowsReport(employee, day.AddDays(1), delivering: true, AssignmentHost.Now));
        Assert.False(access.AllowsReport(Guid.NewGuid(), day, delivering: true, AssignmentHost.Now));
        Assert.False(access.AllowsPhoto(employee, day, declaredForReport: false, AssignmentHost.Now));
        Assert.True(access.AllowsPhoto(employee, day, declaredForReport: true, AssignmentHost.Now));

        try
        {
            host.ModuleTime = AssignmentHost.Now.AddDays(14).AddSeconds(-1);
            using var last = await RefreshAsync(phone, next.RefreshToken);
            var finalToken = await CredentialsAsync(last);
            var (_, finalAccess) = await AuthenticateAsync(finalToken.AccessToken);
            Assert.True(finalAccess!.AllowsReport(employee, day, delivering: true, host.ModuleTime.Value));
            host.ModuleTime = AssignmentHost.Now.AddDays(14);
            using var expired = await RefreshAsync(phone, finalToken.RefreshToken);
            await ReasonAsync(expired, HttpStatusCode.Forbidden, "device-revoked");
            Assert.False((await AuthenticateAsync(finalToken.AccessToken)).Result.Succeeded);
        }
        finally
        {
            host.ModuleTime = null;
        }
    }

    [Theory]
    [InlineData("replacement", "Замена телефона")]
    [InlineData("dismissal", "Увольнение")]
    public async Task Loss_during_delivery_blocks_all_credentials_and_audits_both_revocations(
        string initialReason, string initialLabel)
    {
        var (employee, manager, _) = await SetupAsync();
        using var office = manager;
        using var phone = host.CreateClient();
        var joined = await ConnectAsync(office, phone, employee);
        using var refreshed = await RefreshAsync(phone, joined.RefreshToken);
        var current = await CredentialsAsync(refreshed);
        var originalVersion = await VersionAsync(office, employee);
        using var first = await RevokeAsync(office, joined.DeviceId, originalVersion, initialReason);
        first.EnsureSuccessStatusCode();
        var firstRecord = await JsonAsync(first);

        try
        {
            host.ModuleTime = AssignmentHost.Now.AddHours(1);
            var (_, delivery) = await AuthenticateAsync(current.AccessToken);
            Assert.True(delivery!.AllowsReport(employee, Day("2026-10-31"), delivering: true, host.ModuleTime.Value));
            using var stale = await RevokeAsync(office, joined.DeviceId, originalVersion, "lost");
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.True((await AuthenticateAsync(current.AccessToken)).Result.Succeeded);

            var deliveryVersion = firstRecord.GetProperty("version").GetString();
            using var lost = await RevokeAsync(office, joined.DeviceId, deliveryVersion, "lost");
            lost.EnsureSuccessStatusCode();
            var lostRecord = await JsonAsync(lost);
            Assert.Equal("lost", lostRecord.GetProperty("reason").GetString());
            Assert.Equal(JsonValueKind.Null, lostRecord.GetProperty("deliveryUntil").ValueKind);
            Assert.Equal(host.ModuleTime.Value, lostRecord.GetProperty("revokedAt").GetDateTimeOffset());

            using var previousRefresh = await RefreshAsync(phone, joined.RefreshToken);
            using var activeRefresh = await RefreshAsync(phone, current.RefreshToken);
            await ReasonAsync(previousRefresh, HttpStatusCode.Forbidden, "device-revoked");
            await ReasonAsync(activeRefresh, HttpStatusCode.Forbidden, "device-revoked");
            Assert.False((await AuthenticateAsync(current.AccessToken)).Result.Succeeded);
            using var repeated = await RevokeAsync(office, joined.DeviceId, lostRecord.GetProperty("version").GetString(), "lost");
            Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);

            var (_, reader) = await _world.UserAsync("Аудитор Work", CorePermissions.AuditRead, WorkModule.ReportRead);
            using var auditor = reader;
            using var audit = await auditor.GetAsync("/api/activity?action=work.device.revoked");
            audit.EnsureSuccessStatusCode();
            var page = await JsonAsync(audit);
            Assert.Equal(2, page.GetProperty("total").GetInt32());
            var items = page.GetProperty("items").EnumerateArray().ToArray();
            var initialEvent = Assert.Single(items, item => item.GetProperty("after").GetString() == initialLabel);
            Assert.Equal(JsonValueKind.Null, initialEvent.GetProperty("before").ValueKind);
            var lossEvent = Assert.Single(items, item => item.GetProperty("after").GetString() == "Утеря телефона");
            Assert.Equal(initialLabel, lossEvent.GetProperty("before").GetString());
            Assert.All(items, item =>
            {
                Assert.Equal(firstRecord.GetProperty("revokedBy").GetGuid(), item.GetProperty("actorId").GetGuid());
                Assert.Equal("Администратор устройств", item.GetProperty("actorName").GetString());
                Assert.Equal($"Телефон {joined.DeviceId:D}", item.GetProperty("target").GetString());
                Assert.Equal("ui", item.GetProperty("channel").GetString());
            });
            Assert.DoesNotContain(current.AccessToken, page.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain(current.RefreshToken, page.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain(joined.RefreshToken, page.GetRawText(), StringComparison.Ordinal);
        }
        finally
        {
            host.ModuleTime = null;
        }
    }

    private static object InvitationBody => new { serverUrl = "https://field.example.test", transport = "https-system" };
    private static string DevicePath(Guid employee) => $"/api/work/devices/employees/{employee}";
    private static string InvitationPath(Guid employee) => DevicePath(employee) + "/invitations";

    private async Task<(Guid Employee, HttpClient Manager, Guid Directory)> SetupAsync(bool scoped = false)
    {
        await _world.ResetAsync();
        var directory = await _world.DirectoryAsync();
        var employee = await _world.EmployeeAsync(directory, "Монтажник без учётной записи");
        if (!scoped)
            return (employee, (await _world.UserAsync("Администратор устройств", WorkModule.DevicesManage, WorkModule.SitesAll)).Client,
                directory);
        var site = await _world.SiteAsync("Общая стройка");
        var manager = await _world.EmployeeAsync(directory, "Менеджер стройки");
        var (_, client) = await _world.ManagerAsync(manager, WorkModule.DevicesManage);
        await _world.AssignedAsync(employee, site, AssignmentKind.Installer, "2026-10-01", "2026-11-30");
        await _world.AssignedAsync(manager, site, AssignmentKind.Manager, "2026-10-01", null);
        return (employee, client, directory);
    }

    private async Task<(AuthenticateResult Result, FieldDeviceAccess? Access)> AuthenticateAsync(string token)
    {
        using var scope = host.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Headers.Authorization = "Bearer " + token;
        var result = await context.AuthenticateAsync(FieldDeviceAuthentication.SchemeName);
        return (result, context.Features.Get<FieldDeviceAccess>());
    }

    private static async Task<string> InviteAsync(HttpClient office, Guid employee)
    {
        using var response = await office.PostAsJsonAsync(InvitationPath(employee), InvitationBody);
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl?.NoStore);
        return (await JsonAsync(response)).GetProperty("invitationCode").GetString()!;
    }

    private static Task<HttpResponseMessage> JoinAsync(HttpClient phone, string invitationCode) =>
        phone.PostAsJsonAsync("/api/field/join", new { invitationCode });

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient phone, string refreshToken) =>
        phone.PostAsJsonAsync("/api/field/refresh", new { refreshToken });

    private static async Task<DeviceCredentials> ConnectAsync(HttpClient office, HttpClient phone, Guid employee)
    {
        using var response = await JoinAsync(phone, await InviteAsync(office, employee));
        return await CredentialsAsync(response);
    }

    private static async Task<DeviceCredentials> CredentialsAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl?.NoStore);
        return (await response.Content.ReadFromJsonAsync<DeviceCredentials>())!;
    }

    private static async Task ReasonAsync(HttpResponseMessage response, HttpStatusCode status, string reason)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(reason, (await JsonAsync(response)).GetProperty("reason").GetString());
    }

    private static async Task<string> VersionAsync(HttpClient office, Guid employee)
    {
        using var response = await office.GetAsync(DevicePath(employee));
        response.EnsureSuccessStatusCode();
        return (await JsonAsync(response))[0].GetProperty("version").GetString()!;
    }

    private static async Task<HttpResponseMessage> RevokeAsync(HttpClient office, Guid id, string? version, string? reason)
    {
        using var request = Versioned(HttpMethod.Post, $"/api/work/devices/{id}/revoke", version, new { reason });
        return await office.SendAsync(request);
    }
}
