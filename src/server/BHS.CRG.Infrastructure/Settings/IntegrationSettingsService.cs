using System.Text.Json;
using BHS.CRG.Application.Settings;
using BHS.CRG.Domain.Settings;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace BHS.CRG.Infrastructure.Settings;

public class IntegrationSettingsService(AppDbContext db, IConfiguration config, IMemoryCache cache) : IIntegrationSettings
{
    private const string CacheKey = "integration-settings-effective";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly string[] RecNames = ["Anthropic", "Gemini", "Ollama"];
    private static readonly string[] WebNames = ["Serper", "Yandex"];

    public async Task<IntegrationSettingsModel> GetEffectiveAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out IntegrationSettingsModel? cached) && cached is not null) return cached;
        var raw = await LoadRawAsync(ct);
        var eff = BuildEffective(raw);
        cache.Set(CacheKey, eff);
        return eff;
    }

    public async Task SaveAsync(IntegrationSettingsModel update, CancellationToken ct = default)
    {
        var raw = await LoadRawAsync(ct);

        raw.RecognitionOrder = update.RecognitionOrder;
        raw.FgisDomains = update.FgisDomains;
        raw.ManufacturerDomains = update.ManufacturerDomains;
        MergeEngines(raw.Recognition, update.Recognition);
        MergeEngines(raw.WebSearch, update.WebSearch);

        var json = JsonDocument.Parse(JsonSerializer.Serialize(raw));
        var row = await db.IntegrationSettings.FirstOrDefaultAsync(ct);
        if (row is null) { row = IntegrationSettingsEntity.Create(json); await db.IntegrationSettings.AddAsync(row, ct); }
        else { row.Update(json); db.IntegrationSettings.Update(row); }
        await db.SaveChangesAsync(ct);
        Invalidate();
    }

    public void Invalidate() => cache.Remove(CacheKey);

    // Ключи перезаписываем только при непустом новом значении (UI не присылает существующие ключи).
    private static void MergeEngines(Dictionary<string, IntegrationEngine> target, Dictionary<string, IntegrationEngine> update)
    {
        foreach (var (name, u) in update)
        {
            var existing = target.TryGetValue(name, out var e) ? e : new IntegrationEngine();
            target[name] = new IntegrationEngine
            {
                Enabled = u.Enabled,
                Model = u.Model,
                BaseUrl = u.BaseUrl,
                FolderId = u.FolderId,
                Host = u.Host,
                ApiKey = string.IsNullOrWhiteSpace(u.ApiKey) ? existing.ApiKey : u.ApiKey,
            };
        }
    }

    private async Task<IntegrationSettingsModel> LoadRawAsync(CancellationToken ct)
    {
        var row = await db.IntegrationSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (row is null) return new IntegrationSettingsModel();
        return JsonSerializer.Deserialize<IntegrationSettingsModel>(row.Data.RootElement.GetRawText(), JsonOpts) ?? new IntegrationSettingsModel();
    }

    private IntegrationSettingsModel BuildEffective(IntegrationSettingsModel raw)
    {
        var m = new IntegrationSettingsModel
        {
            RecognitionOrder = raw.RecognitionOrder.Count > 0
                ? raw.RecognitionOrder
                : (config.GetSection("Recognition:Order").Get<string[]>() ?? ["Gemini", "Anthropic", "Ollama"]).ToList(),
            FgisDomains = raw.FgisDomains.Count > 0 ? raw.FgisDomains : (config.GetSection("WebSearch:FgisDomains").Get<string[]>() ?? ["pub.fsa.gov.ru", "fsa.gov.ru"]).ToList(),
            ManufacturerDomains = raw.ManufacturerDomains.Count > 0 ? raw.ManufacturerDomains : (config.GetSection("WebSearch:ManufacturerDomains").Get<string[]>() ?? []).ToList(),
        };

        foreach (var name in RecNames) m.Recognition[name] = EffRec(name, raw);
        foreach (var name in WebNames) m.WebSearch[name] = EffWeb(name, raw);
        return m;
    }

    private IntegrationEngine EffRec(string name, IntegrationSettingsModel raw)
    {
        var has = raw.Recognition.TryGetValue(name, out var r);
        r ??= new IntegrationEngine();
        var e = new IntegrationEngine
        {
            ApiKey = Pick(r.ApiKey, name switch { "Anthropic" => config["Anthropic:ApiKey"], "Gemini" => config["Gemini:ApiKey"], _ => null }),
            Model = Pick(r.Model, name switch
            {
                "Anthropic" => config["Anthropic:Model"] ?? "claude-sonnet-4-6",
                "Gemini" => config["Gemini:Model"] ?? "gemini-2.5-flash",
                "Ollama" => config["Ollama:Model"],
                _ => null,
            }),
            BaseUrl = Pick(r.BaseUrl, name == "Ollama" ? (config["Ollama:BaseUrl"] ?? "http://localhost:11434") : null),
        };
        e.Enabled = has ? r.Enabled : HasKey(name, e);
        return e;
    }

    private IntegrationEngine EffWeb(string name, IntegrationSettingsModel raw)
    {
        var has = raw.WebSearch.TryGetValue(name, out var r);
        r ??= new IntegrationEngine();
        var e = new IntegrationEngine
        {
            ApiKey = Pick(r.ApiKey, name switch { "Serper" => config["WebSearch:ApiKey"], "Yandex" => config["WebSearch:Yandex:ApiKey"], _ => null }),
            FolderId = Pick(r.FolderId, name == "Yandex" ? config["WebSearch:Yandex:FolderId"] : null),
            Host = Pick(r.Host, name == "Yandex" ? (config["WebSearch:Yandex:Host"] ?? "https://yandex.ru/search/xml") : null),
        };
        e.Enabled = has ? r.Enabled : HasKey(name, e);
        return e;
    }

    private static bool HasKey(string name, IntegrationEngine e) => name switch
    {
        "Ollama" => !string.IsNullOrWhiteSpace(e.Model),
        "Yandex" => !string.IsNullOrWhiteSpace(e.ApiKey) && !string.IsNullOrWhiteSpace(e.FolderId),
        _ => !string.IsNullOrWhiteSpace(e.ApiKey),
    };

    private static string? Pick(string? primary, string? fallback) => string.IsNullOrWhiteSpace(primary) ? fallback : primary;
}
