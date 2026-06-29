namespace BHS.CRG.Application.Settings;

/// <summary>Настройки одного движка (распознавание/поиск).</summary>
public class IntegrationEngine
{
    public bool Enabled { get; set; }
    public string? ApiKey { get; set; }
    public string? Model { get; set; }     // Anthropic/Gemini/Ollama
    public string? BaseUrl { get; set; }   // Ollama
    public string? FolderId { get; set; }  // Yandex
    public string? Host { get; set; }      // Yandex
}

/// <summary>
/// Управляемые из UI настройки интеграций (распознавание + веб-поиск).
/// Хранятся в БД; пустой ключ движка означает fallback на конфигурацию (user-secrets/appsettings).
/// </summary>
public class IntegrationSettingsModel
{
    public List<string> RecognitionOrder { get; set; } = [];
    /// <summary>Anthropic / Gemini / Ollama.</summary>
    public Dictionary<string, IntegrationEngine> Recognition { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Serper / Yandex.</summary>
    public Dictionary<string, IntegrationEngine> WebSearch { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> FgisDomains { get; set; } = [];
    public List<string> ManufacturerDomains { get; set; } = [];

    public IntegrationEngine Rec(string name)
        => Recognition.TryGetValue(name, out var e) ? e : new IntegrationEngine();
    public IntegrationEngine Web(string name)
        => WebSearch.TryGetValue(name, out var e) ? e : new IntegrationEngine();
}

/// <summary>
/// Эффективные настройки интеграций (БД поверх конфигурации). Кэшируется, сбрасывается при сохранении.
/// </summary>
public interface IIntegrationSettings
{
    Task<IntegrationSettingsModel> GetEffectiveAsync(CancellationToken ct = default);
    Task SaveAsync(IntegrationSettingsModel update, CancellationToken ct = default);
    void Invalidate();
}
