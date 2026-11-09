using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Приложение к смете так, как его делает клиент (change <c>plan-11-conjuncture</c>): загрузка файла в смету, опрос
/// отчёта до исхода разбора, принятие отчёта.
/// </summary>
internal static class ConjunctureFlow
{
    public const string Ka = "ka-16.2.0-vn-summary";
    public const string Vor = "vor-15.3.7-no-attachments";

    public static MultipartFormDataContent Form(byte[] file, string fileName)
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", fileName);
        return form;
    }

    public static async Task<HttpResponseMessage> PostUploadAsync(HttpClient client, Guid estimateId, byte[] file, string fileName)
    {
        using var form = Form(file, fileName);
        return await client.PostAsync($"/api/plan/estimates/{estimateId}/conjuncture", form);
    }

    /// <summary>Загрузить файл в смету и дождаться отчёта разбора.</summary>
    public static async Task<JsonElement> ParsedAsync(HttpClient client, Guid estimateId, byte[] file, string fileName)
    {
        var response = await PostUploadAsync(client, estimateId, file, fileName);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await EstimateImportFlow.OutcomeAsync(client, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    public static Task<JsonElement> ParsedAsync(HttpClient client, Guid estimateId, string sample) =>
        ParsedAsync(client, estimateId, GrandSmetaSamples.Gsfx(sample), sample + ".gsfx");

    public static Task<HttpResponseMessage> AcceptAsync(HttpClient client, Guid estimateId, Guid importId, bool acceptPartial = false) =>
        client.PostAsJsonAsync($"/api/plan/estimates/{estimateId}/conjuncture/{importId}/accept", new { acceptPartial });

    /// <summary>Загрузить, дождаться разбора и принять — ответ принятия.</summary>
    public static async Task<JsonElement> AttachedAsync(HttpClient client, Guid estimateId, string sample)
    {
        var report = await ParsedAsync(client, estimateId, sample);
        var response = await AcceptAsync(client, estimateId, EstimateImportFlow.Id(report));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("attachment");
    }

    public static Task<JsonElement> ItemsAsync(HttpClient client, Guid analysisId, int? limit = null) =>
        client.GetFromJsonAsync<JsonElement>($"/api/plan/conjuncture/{analysisId}/items" + (limit is { } l ? $"?limit={l}" : ""));

    public static HttpRequestMessage Delete(string path, string? version)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, path);
        if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", version);
        return request;
    }

    public static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    public static string Version(JsonElement e) => e.GetProperty("version").GetString()!;
}
