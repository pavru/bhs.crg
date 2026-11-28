using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Devices;

namespace BHS.CRG.Tests.Integration;

internal sealed class FieldReportClient(HttpClient client)
{
    internal const string Path = FieldReportEndpoints.Path;

    internal static async Task<DeviceCredentials> ConnectAsync(HttpClient office, HttpClient phone, Guid employee)
    {
        using var invite = await office.PostAsJsonAsync($"/api/work/devices/employees/{employee}/invitations",
            new { serverUrl = "https://field.example.test", transport = "https-system" });
        var invitationCode = (await ReadAsync(invite, HttpStatusCode.OK)).GetProperty("invitationCode").GetString();
        using var joined = await phone.PostAsJsonAsync("/api/field/join", new { invitationCode });
        var credentials = (await ReadAsync(joined, HttpStatusCode.OK)).Deserialize<DeviceCredentials>(WebJson)!;
        Assert.Equal(employee, credentials.EmployeeId);
        Authorize(phone, credentials);
        return credentials;
    }

    internal static async Task<DeviceCredentials> RotateAsync(HttpClient phone, DeviceCredentials credentials)
    {
        using var response = await RefreshAsync(phone, credentials.RefreshToken);
        var next = (await ReadAsync(response, HttpStatusCode.OK)).Deserialize<DeviceCredentials>(WebJson)!;
        Authorize(phone, next);
        return next;
    }

    internal static Task<HttpResponseMessage> RefreshAsync(HttpClient phone, string refreshToken) =>
        phone.PostAsJsonAsync("/api/field/refresh", new { refreshToken });

    private static void Authorize(HttpClient phone, DeviceCredentials credentials) =>
        phone.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);

    internal static async Task RevokeAsync(HttpClient office, DeviceCredentials credentials, string reason)
    {
        var devices = await office.GetFromJsonAsync<JsonElement>(
            $"/api/work/devices/employees/{credentials.EmployeeId}");
        var version = Assert.Single(devices.EnumerateArray(), device =>
            device.GetProperty("deviceId").GetGuid() == credentials.DeviceId).GetProperty("version").GetString();
        using var request = AssignmentWorld.Versioned(HttpMethod.Post,
            $"/api/work/devices/{credentials.DeviceId}/revoke", version, new { reason });
        using var response = await office.SendAsync(request);
        await ReadAsync(response, HttpStatusCode.OK);
    }

    internal Task<HttpResponseMessage> CreateAsync(object body, int code = WorkModule.MinimumFieldSenderVersionCode) =>
        SendAsync(HttpMethod.Post, Path, null, body, code);

    internal Task<HttpResponseMessage> ChangeAsync(Guid id, string? version, object body) =>
        SendAsync(HttpMethod.Put, $"{Path}/{id}", version, body);

    internal Task<HttpResponseMessage> SubmitAsync(Guid id, string? version, Guid changeId,
        DateTimeOffset? deviceTime = null, bool confirmManagerEdit = false) =>
        SendAsync(HttpMethod.Post, $"{Path}/{id}/submit", version, new { changeId, deviceTime, confirmManagerEdit });

    internal Task<HttpResponseMessage> GetAsync(Guid id) => client.GetAsync($"{Path}/{id}");

    internal Task<JsonElement> FeedAsync(long since = 0) => client.GetFromJsonAsync<JsonElement>($"{Path}?since={since}");

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod verb, string path, string? version, object body, int code = WorkModule.MinimumFieldSenderVersionCode)
    {
        using var request = AssignmentWorld.Versioned(verb, path, version, body);
        request.Headers.Add("X-Field-Version-Code", code.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return await client.SendAsync(request);
    }

    internal static async Task<JsonElement> ReadAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Ожидался {status}, пришёл {response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    internal static async Task RefusedAsync(Task<HttpResponseMessage> responseTask, HttpStatusCode status, string reason)
    {
        using var response = await responseTask;
        Assert.Equal(reason, (await ReadAsync(response, status)).GetProperty("reason").GetString());
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
}
