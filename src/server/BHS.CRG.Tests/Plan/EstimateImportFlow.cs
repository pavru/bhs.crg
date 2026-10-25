using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Импорт сметы так, как его делает клиент (change <c>plan-07-estimate-import</c>): форма с файлом и целью,
/// затем опрос отчёта до исхода разбора.
/// </summary>
internal static class EstimateImportFlow
{
    /// <summary>Сметчик: правка смет без права на себестоимость.</summary>
    public static Task<HttpClient> EditorAsync(PlanOnlyHost host) =>
        GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead, PlanModule.EstimateEdit);

    /// <summary>Сметчик с себестоимостью — видит итоги.</summary>
    public static Task<HttpClient> CostEditorAsync(PlanOnlyHost host) =>
        GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead, PlanModule.EstimateEdit, PlanModule.EstimateCost);

    /// <summary>Форма загрузки: поля цели — как их назовёт клиент; <c>null</c> — поля нет.</summary>
    public static MultipartFormDataContent Form(
        byte[] file, string fileName, Guid? constructionId, Guid? estimateId = null, string? newName = null,
        Guid? sectionId = null, string? kind = null)
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", fileName);
        void Field(string name, object? value)
        {
            if (value is not null) form.Add(new StringContent(value.ToString()!), name);
        }
        Field("constructionId", constructionId);
        Field("estimateId", estimateId);
        Field("newEstimate.name", newName);
        Field("newEstimate.sectionId", sectionId);
        Field("newEstimate.kind", kind);
        return form;
    }

    /// <summary>Загрузить образец в новую смету стройки; ответ — отчёт загрузки.</summary>
    public static Task<JsonElement> UploadAsync(HttpClient client, Guid constructionId, string sample) =>
        UploadAsync(client, Form(GrandSmetaSamples.Gsfx(sample), sample + ".gsfx", constructionId, newName: "Из файла"));

    public static async Task<JsonElement> UploadAsync(HttpClient client, MultipartFormDataContent form)
    {
        using (form)
        {
            var response = await client.PostAsync("/api/plan/imports", form);
            Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
    }

    public static Task<JsonElement> GetAsync(HttpClient client, Guid id) =>
        client.GetFromJsonAsync<JsonElement>($"/api/plan/imports/{id}");

    /// <summary>Дождаться исхода разбора — тем же опросом, каким его ждёт экран импорта.</summary>
    public static async Task<JsonElement> OutcomeAsync(HttpClient client, JsonElement uploaded)
    {
        var id = Id(uploaded);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            var report = await GetAsync(client, id);
            if (report.GetProperty("state").GetString() != "parsing") return report;
            Assert.True(DateTime.UtcNow < deadline, $"Разбор импорта {id} так и не закончился.");
            await Task.Delay(50);
        }
    }

    public static Guid Id(JsonElement import) => import.GetProperty("id").GetGuid();

    public static string[] Marks(JsonElement import) =>
        [.. import.GetProperty("marks").EnumerateArray().Select(m => m.GetProperty("code").GetString()!)];

    /// <summary>Образец, у первых <paramref name="count" /> позиций которого чужой атрибут: они не пройдут схему.</summary>
    public static byte[] WithBrokenPositions(string sample, int count)
    {
        var text = GrandSmetaSamples.DataXmlText(sample);
        var at = 0;
        for (var i = 0; i < count; i++)
        {
            at = text.IndexOf("<Position ", at, StringComparison.Ordinal);
            Assert.True(at >= 0, $"В образце {sample} меньше {count} позиций.");
            text = text[..at] + "<Position Bogus=\"1\" " + text[(at + "<Position ".Length)..];
            at += "<Position Bogus".Length;
        }
        return GrandSmetaSamples.Cp1251.GetBytes(text);
    }

    public static HttpRequestMessage Delete(Guid id, string? version)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/plan/imports/{id}");
        if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", version);
        return request;
    }
}
