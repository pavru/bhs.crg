using System.Net;
using System.Text;
using System.Text.Json;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Settings;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>Движок распознавания через Google Gemini (vision + PDF). Настройки — из IIntegrationSettings.</summary>
public class GeminiRecognizerEngine(
    HttpClient http, IIntegrationSettings settings, ILogger<GeminiRecognizerEngine> logger
) : IRecognizerEngine
{
    public string Name => "Gemini";

    public async Task<string> RecognizeRawAsync(byte[] file, string mimeType, IReadOnlyList<RecognitionField> fields,
        Func<IReadOnlyList<RecognitionField>, string>? promptBuilder = null, CancellationToken ct = default)
    {
        var cfg = (await settings.GetEffectiveAsync(ct)).Rec("Gemini");
        var apiKey = cfg.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new RecognitionUnavailableException("Не задан ключ Gemini.");
        var model = string.IsNullOrWhiteSpace(cfg.Model) ? "gemini-2.5-flash" : cfg.Model;

        var mt = string.Equals(mimeType, "application/pdf", StringComparison.OrdinalIgnoreCase)
            ? "application/pdf"
            : RecognitionShared.ImageTypes.Contains(mimeType) ? RecognitionShared.NormalizeImageMime(mimeType)
            : throw new RecognitionUnavailableException($"Gemini: формат не поддерживается: {mimeType}");

        var requestBody = new
        {
            contents = new object[]
            {
                new { parts = new object[]
                {
                    new { inline_data = new { mime_type = mt, data = Convert.ToBase64String(file) } },
                    new { text = (promptBuilder ?? RecognitionShared.BuildPrompt)(fields) },
                } },
            },
            generationConfig = new { response_mime_type = "application/json", temperature = 0 },
        };
        var json = JsonSerializer.Serialize(requestBody);
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            HttpResponseMessage resp;
            try { resp = await http.SendAsync(req, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt >= maxAttempts) throw new RecognitionUnavailableException($"Gemini: ошибка обращения: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct); continue;
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode) return ExtractText(body);

            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                if (attempt < maxAttempts) { await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct); continue; }
                logger.LogWarning("Gemini лимит: {Body}", RecognitionShared.Truncate(body, 300));
                throw new RecognitionLimitException("Gemini: достигнут лимит запросов.");
            }
            if ((int)resp.StatusCode >= 500 && attempt < maxAttempts) { await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct); continue; }

            throw new RecognitionUnavailableException($"Gemini ответил {(int)resp.StatusCode}: {RecognitionShared.Truncate(body, 300)}");
        }
    }

    private static string ExtractText(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (doc.RootElement.TryGetProperty("candidates", out var cands) && cands.ValueKind == JsonValueKind.Array)
            foreach (var c in cands.EnumerateArray())
                if (c.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                {
                    var sb = new StringBuilder();
                    foreach (var p in parts.EnumerateArray())
                        if (p.TryGetProperty("text", out var t)) sb.Append(t.GetString());
                    return sb.ToString();
                }
        return "";
    }
}
