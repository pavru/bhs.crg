using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Tests.Common;

namespace BHS.CRG.Tests.Plan;

internal static class CalendarRequests
{
    public static string Company(int year) => $"/api/plan/calendars/company/{year}";
    public static string Construction(Guid id) => $"/api/plan/calendars/constructions/{id}";
    public static string Version(JsonElement calendar) => calendar.GetProperty("version").GetString()!;

    public static byte[] Sample(int year) => File.ReadAllBytes(RepoPaths.File(
        "src", "server", "BHS.CRG.Tests", "TestData", "Calendars", $"ppt-ru-{year}.ics"));

    public static async Task<HttpResponseMessage> PostFileAsync(HttpClient client, int year, byte[] bytes)
    {
        using var file = new MultipartFormDataContent();
        file.Add(new ByteArrayContent(bytes), "file", $"{year}.ics");
        return await client.PostAsync($"{Company(year)}/import", file);
    }

    public static async Task<JsonElement> PreviewAsync(HttpClient client, int year, byte[] bytes)
    {
        using var response = await PostFileAsync(client, year, bytes);
        return await ReadAsync(response);
    }

    public static async Task<HttpResponseMessage> PutAsync(HttpClient client, string url, string? version, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        if (version is not null) Assert.True(request.Headers.TryAddWithoutValidation("If-Match", version));
        return await client.SendAsync(request);
    }

    public static async Task<JsonElement> SaveCompanyAsync(HttpClient client, int year, string version, params object[] days)
    {
        using var response = await PutAsync(client, Company(year), version, new { workingWeek = 31, hoursPerShift = 8, days });
        return await ReadAsync(response);
    }

    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
