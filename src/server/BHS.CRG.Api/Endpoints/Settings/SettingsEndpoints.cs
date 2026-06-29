using System.Text.Json;
using BHS.CRG.Application.Settings;

namespace BHS.CRG.Api.Endpoints.Settings;

public static class SettingsEndpoints
{
    // Курируемые списки моделей для облачных движков (их каталог не перечисляем по сети).
    private static readonly string[] AnthropicModels =
        ["claude-opus-4-8", "claude-sonnet-4-6", "claude-haiku-4-5-20251001"];
    private static readonly string[] GeminiModels =
        ["gemini-2.5-pro", "gemini-2.5-flash", "gemini-2.5-flash-lite", "gemini-2.0-flash"];

    public static void MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/settings/integrations").RequireAuthorization("Admin");

        // Чтение: ключи НЕ возвращаем, только признак «ключ задан».
        g.MapGet("/", async (IIntegrationSettings settings) =>
        {
            var m = await settings.GetEffectiveAsync();
            return Results.Ok(new
            {
                recognitionOrder = m.RecognitionOrder,
                recognition = m.Recognition.ToDictionary(kv => kv.Key, kv => Mask(kv.Value)),
                webSearch = m.WebSearch.ToDictionary(kv => kv.Key, kv => Mask(kv.Value)),
                fgisDomains = m.FgisDomains,
                manufacturerDomains = m.ManufacturerDomains,
            });
        });

        // Доступные модели для выпадающих списков. Ollama — только реально скачанные (через /api/tags).
        g.MapGet("/models", async (IIntegrationSettings settings, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var m = await settings.GetEffectiveAsync(ct);
            var ollama = await GetOllamaModelsAsync(m.Rec("Ollama").BaseUrl, httpFactory, ct);
            return Results.Ok(new
            {
                anthropic = AnthropicModels,
                gemini = GeminiModels,
                ollama,
            });
        });

        // Сохранение: ключ перезаписывается только при непустом значении (пустой = оставить прежний).
        g.MapPut("/", async (IntegrationSettingsModel model, IIntegrationSettings settings) =>
        {
            await settings.SaveAsync(model);
            return Results.NoContent();
        });
    }

    private static async Task<string[]> GetOllamaModelsAsync(string? baseUrl, IHttpClientFactory httpFactory, CancellationToken ct)
    {
        var url = (string.IsNullOrWhiteSpace(baseUrl) ? "http://localhost:11434" : baseUrl).TrimEnd('/') + "/api/tags";
        try
        {
            using var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(5);
            using var stream = await http.GetStreamAsync(url, ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                return [];
            return models.EnumerateArray()
                .Select(e => e.TryGetProperty("name", out var n) ? n.GetString() : null)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .ToArray();
        }
        catch
        {
            // Ollama не запущен / недоступен — пустой список (UI покажет подсказку).
            return [];
        }
    }

    private static object Mask(IntegrationEngine e) => new
    {
        enabled = e.Enabled,
        hasKey = !string.IsNullOrWhiteSpace(e.ApiKey),
        model = e.Model,
        baseUrl = e.BaseUrl,
        folderId = e.FolderId,
        host = e.Host,
    };
}
