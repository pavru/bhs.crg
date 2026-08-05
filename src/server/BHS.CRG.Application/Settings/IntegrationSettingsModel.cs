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

/// <summary>Настройки SMTP для исходящей почты. Пароль хранится в том же JSON-store, что и API-ключи.</summary>
public class SmtpSettings
{
    public bool Enabled { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? User { get; set; }
    public string? Password { get; set; }
    /// <summary>Адрес отправителя (From).</summary>
    public string? From { get; set; }
    /// <summary>Отображаемое имя отправителя.</summary>
    public string? FromName { get; set; }
    /// <summary>true — STARTTLS/SSL (обычно порт 587/465); false — без шифрования.</summary>
    public bool UseSsl { get; set; } = true;

    /// <summary>
    /// Тот же самый почтовый сервер и та же учётная запись на нём?
    ///
    /// От этого зависит судьба СОХРАНЁННОГО пароля: наследовать его можно только на тот же сервер.
    /// Иначе достаточно указать чужой хост с пустым паролем — и сохранённый уедет туда сам.
    /// <c>UseSsl</c> входит в сравнение намеренно: снятие шифрования — тоже смена адресата, только
    /// адресатом становится любой на пути, а MailKit отдаёт пароль по AUTH и без TLS.
    ///
    /// Определение здесь, а не в двух вызывающих (сохранение и проверка связи): разъехавшись, они
    /// оставили бы дыру ровно в том месте, ради которого написаны.
    /// </summary>
    public bool SameServerAs(SmtpSettings other) =>
        string.Equals(Host?.Trim(), other.Host?.Trim(), StringComparison.OrdinalIgnoreCase)
        && Port == other.Port
        && string.Equals(User?.Trim(), other.User?.Trim(), StringComparison.Ordinal)
        && UseSsl == other.UseSsl;
}

/// <summary>
/// Управляемые из UI настройки интеграций (распознавание + веб-поиск + почта).
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
    public SmtpSettings Smtp { get; set; } = new();

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
    /// <summary>Сохраняет только секцию SMTP (не трогая распознавание/поиск) — отдельные формы не затирают друг друга.</summary>
    Task SaveSmtpAsync(SmtpSettings smtp, CancellationToken ct = default);
    void Invalidate();
}
