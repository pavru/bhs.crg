using System.Text.Json;
using BHS.CRG.Application.Settings;
using BHS.CRG.Domain.Settings;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace BHS.CRG.Infrastructure.Settings;

public class IntegrationSettingsService(
    AppDbContext db, IConfiguration config, IMemoryCache cache, SettingsSecretProtector secrets) : IIntegrationSettings
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
        // Smtp у SaveAsync НЕ трогаем — им управляет отдельный SaveSmtpAsync (иначе форма распознавания
        // без секции SMTP затирала бы её дефолтом). Сохраняем прежнее значение.
        await PersistRawAsync(raw, ct);
    }

    public async Task SaveSmtpAsync(SmtpSettings smtp, CancellationToken ct = default)
    {
        var raw = await LoadRawAsync(ct);
        raw.Smtp = MergeSmtp(raw.Smtp, smtp);
        await PersistRawAsync(raw, ct);
    }

    /// <summary>
    /// Перешифровать секреты, оставшиеся открытыми от версий до 0.92.0. Вызывается один раз при
    /// старте (см. Program.cs). Сделано отдельным проходом, а не при чтении: запись на пути чтения
    /// удивляет, а первое чтение вполне может случиться в двух экземплярах приложения разом.
    /// </summary>
    /// <returns>Сколько значений перешифровано; ноль — работы не было.</returns>
    public async Task<int> ProtectStoredSecretsAsync(CancellationToken ct = default)
    {
        var row = await db.IntegrationSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (row is null) return 0;

        var raw = JsonSerializer.Deserialize<IntegrationSettingsModel>(row.Data.RootElement.GetRawText(), JsonOpts);
        if (raw is null) return 0;

        var plain = CountPlainSecrets(raw);
        if (plain == 0) return 0;

        // Значения здесь ещё НЕ расшифрованы (читали в обход LoadRawAsync); Protect пропускает
        // уже зашифрованные и трогает только открытые.
        await PersistRawAsync(raw, ct);
        return plain;
    }

    private static int CountPlainSecrets(IntegrationSettingsModel raw)
    {
        var n = 0;
        foreach (var e in raw.Recognition.Values) if (IsPlain(e.ApiKey)) n++;
        foreach (var e in raw.WebSearch.Values) if (IsPlain(e.ApiKey)) n++;
        if (IsPlain(raw.Smtp.Password)) n++;
        return n;

        static bool IsPlain(string? v) => !string.IsNullOrWhiteSpace(v) && !SettingsSecretProtector.IsProtected(v);
    }

    /// <summary>Секреты шифруем перед записью; остальные поля идут как есть.</summary>
    private void ProtectSecrets(IntegrationSettingsModel m)
    {
        foreach (var e in m.Recognition.Values) e.ApiKey = secrets.Protect(e.ApiKey);
        foreach (var e in m.WebSearch.Values) e.ApiKey = secrets.Protect(e.ApiKey);
        m.Smtp.Password = secrets.Protect(m.Smtp.Password);
    }

    /// <summary>Обратное к <see cref="ProtectSecrets"/>: наружу модель всегда отдаётся расшифрованной.</summary>
    private void UnprotectSecrets(IntegrationSettingsModel m)
    {
        foreach (var e in m.Recognition.Values) e.ApiKey = secrets.Unprotect(e.ApiKey);
        foreach (var e in m.WebSearch.Values) e.ApiKey = secrets.Unprotect(e.ApiKey);
        m.Smtp.Password = secrets.Unprotect(m.Smtp.Password);
    }

    private async Task PersistRawAsync(IntegrationSettingsModel raw, CancellationToken ct)
    {
        ProtectSecrets(raw);
        var json = JsonDocument.Parse(JsonSerializer.Serialize(raw));
        var row = await db.IntegrationSettings.FirstOrDefaultAsync(ct);
        if (row is null) { row = IntegrationSettingsEntity.Create(json); await db.IntegrationSettings.AddAsync(row, ct); }
        else { row.Update(json); db.IntegrationSettings.Update(row); }
        await db.SaveChangesAsync(ct);
        Invalidate();
    }

    public void Invalidate() => cache.Remove(CacheKey);

    // Пароль SMTP перезаписываем только при непустом новом значении (UI не присылает существующий, как и ключи).
    private static SmtpSettings MergeSmtp(SmtpSettings existing, SmtpSettings update) => new()
    {
        Enabled = update.Enabled,
        Host = update.Host,
        Port = update.Port,
        User = update.User,
        From = update.From,
        FromName = update.FromName,
        UseSsl = update.UseSsl,
        Password = string.IsNullOrWhiteSpace(update.Password) ? existing.Password : update.Password,
    };

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
        var m = JsonSerializer.Deserialize<IntegrationSettingsModel>(row.Data.RootElement.GetRawText(), JsonOpts) ?? new IntegrationSettingsModel();
        // Внутри приложения модель ходит расшифрованной: шифрование — свойство хранения, и знать о
        // нём движкам распознавания, поиску и почте незачем. Обратно шифрует PersistRawAsync.
        UnprotectSecrets(m);
        return m;
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
            Smtp = raw.Smtp, // SMTP настраивается из UI; config-fallback пока не требуется
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
