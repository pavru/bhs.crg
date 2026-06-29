using System.Text;
using System.Text.Json;
using BHS.CRG.Application.Settings;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Search;

/// <summary>Движок веб-поиска через Serper.dev (выдача Google). Настройки — из IIntegrationSettings.</summary>
public class SerperEngine(
    HttpClient http, IIntegrationSettings settings, ILogger<SerperEngine> logger
) : IWebSearchEngine
{
    private const string ApiUrl = "https://google.serper.dev/search";

    public string Name => "Serper";

    public async Task<IReadOnlyList<WebHit>> QueryAsync(string query, CancellationToken ct = default)
    {
        var apiKey = (await settings.GetEffectiveAsync(ct)).Web("Serper").ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey)) return [];
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            req.Headers.TryAddWithoutValidation("X-API-KEY", apiKey);
            req.Content = new StringContent(JsonSerializer.Serialize(new { q = query, num = 10, gl = "ru", hl = "ru" }), Encoding.UTF8, "application/json");
            var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Serper {Status}: {Body}", resp.StatusCode, body.Length > 200 ? body[..200] : body);
                return [];
            }
            var list = new List<WebHit>();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("organic", out var organic) && organic.ValueKind == JsonValueKind.Array)
                foreach (var item in organic.EnumerateArray())
                {
                    var link = item.TryGetProperty("link", out var l) ? l.GetString() : null;
                    if (string.IsNullOrWhiteSpace(link)) continue;
                    list.Add(new WebHit(
                        item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                        link,
                        item.TryGetProperty("snippet", out var s) ? s.GetString() ?? "" : ""));
                }
            return list;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Serper-запрос не выполнен");
            return [];
        }
    }
}
