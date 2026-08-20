using BHS.CRG.Application.Notifications;
using BHS.CRG.Application.Settings;
using BHS.CRG.Domain.Notifications;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;

namespace BHS.CRG.Api.Notifications;

/// <summary>
/// Периодически проверяет состояние БД, хранилища MinIO и (если включён) Ollama.
/// Хранит актуальный снимок (<see cref="IHealthState"/>) и публикует уведомление
/// при смене состояния компонента (упал → Ошибка/Предупреждение, восстановился → Информация).
/// </summary>
public class HealthMonitorService(
    IServiceScopeFactory scopeFactory,
    IMinioClient minio,
    BlobStorageOptions blobOptions,
    IHttpClientFactory httpFactory,
    ILogger<HealthMonitorService> logger
) : BackgroundService, IHealthState
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(5);

    private volatile IReadOnlyList<ComponentHealth> _snapshot = [];
    private readonly Dictionary<string, bool> _previous = new();

    public IReadOnlyList<ComponentHealth> Snapshot => _snapshot;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(StartDelay, ct); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { await TickAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Сбой цикла health-мониторинга"); }
        }
        while (await SafeWait(timer, ct));
    }

    private static async Task<bool> SafeWait(PeriodicTimer t, CancellationToken ct)
    {
        try { return await t.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var settings = await sp.GetRequiredService<IIntegrationSettings>().GetEffectiveAsync(ct);

        var checks = new List<ComponentHealth>
        {
            await CheckAsync("База данных", () => CheckPostgresAsync(sp, ct)),
            await CheckAsync("Хранилище (MinIO)", () => CheckMinioAsync(ct)),
        };

        // Движки распознавания проверяем ровно те, что РЕАЛЬНО участвуют в работе, — тем же
        // правилом, что и цепочка (EngineReadiness, issue #797). Раньше условия были свои: Ollama
        // проверялась при одной галке «включён», и без выбранной модели мониторинг сообщал бы о
        // недоступности движка, которым система всё равно не пользуется.
        var ollama = settings.Rec("Ollama");
        if (EngineReadiness.IsUsableForRecognition("Ollama", ollama))
            checks.Add(await CheckAsync("Ollama (распознавание)", async () =>
            {
                await CheckOllamaAsync(ollama.BaseUrl, ct);
                return await ModelDetailAsync(sp, "Ollama", ollama, ct);
            }));

        // Gemini — сначала лёгкий GET метаданных (доступен ли движок вообще), потом проверка самой
        // модели. Вторая стоит одного запроса генерации на ответ (см. ModelDetailAsync): метаданные
        // про снятую с обслуживания модель молчат, и без пробы мониторинг её не увидит.
        var gemini = settings.Rec("Gemini");
        if (EngineReadiness.IsUsableForRecognition("Gemini", gemini))
            checks.Add(await CheckAsync("Gemini (распознавание)", async () =>
            {
                await CheckGeminiAsync(gemini.ApiKey!, gemini.Model, ct);
                return await ModelDetailAsync(sp, "Gemini", gemini, ct);
            }));

        _snapshot = checks;

        var notifier = sp.GetRequiredService<INotificationService>();
        foreach (var c in checks)
        {
            var known = _previous.TryGetValue(c.Name, out var prev);
            if (!known)
            {
                if (!c.Healthy)
                    await notifier.PublishAsync(SeverityFor(c.Name), $"{c.Name}: недоступен",
                        c.Detail ?? "Компонент не отвечает.", "Состояние системы", ct: ct);
            }
            else if (prev != c.Healthy)
            {
                if (c.Healthy)
                    await notifier.PublishAsync(NotificationSeverity.Info, $"{c.Name}: восстановлен",
                        "Компонент снова доступен.", "Состояние системы", ct: ct);
                else
                    await notifier.PublishAsync(SeverityFor(c.Name), $"{c.Name}: недоступен",
                        c.Detail ?? "Компонент перестал отвечать.", "Состояние системы", ct: ct);
            }
            _previous[c.Name] = c.Healthy;
        }
    }

    // Движки распознавания (Ollama/Gemini) → Предупреждение; ядро (БД/MinIO) → Ошибка.
    private static NotificationSeverity SeverityFor(string name)
        => name.StartsWith("Ollama") || name.StartsWith("Gemini")
            ? NotificationSeverity.Warning
            : NotificationSeverity.Error;

    private static async Task<ComponentHealth> CheckAsync(string name, Func<Task<string?>> probe)
    {
        try
        {
            var detail = await probe();
            return new ComponentHealth(name, true, detail, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            return new ComponentHealth(name, false, Short(ex.Message), DateTimeOffset.UtcNow);
        }
    }

    private static async Task<string?> CheckPostgresAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<AppDbContext>();
        if (!await db.Database.CanConnectAsync(ct))
            throw new InvalidOperationException("Нет соединения с PostgreSQL");
        return null;
    }

    private async Task<string?> CheckMinioAsync(CancellationToken ct)
    {
        await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(blobOptions.Bucket), ct);
        return null;
    }

    /// <summary>
    /// Отвечать движок может и с моделью, которой у поставщика больше нет, — тогда мониторинг молчит,
    /// а каждый документ получает отказ. Проверки доступности этого не ловят по устройству: метаданные
    /// модели Google отдаёт с кодом 200 и после того, как generateContent начал отвечать 404
    /// (issue #799), а Ollama на <c>/api/tags</c> исправно перечисляет то, что скачано, не сверяя со
    /// своими настройками.
    ///
    /// Для облачного движка это РАСХОД: проба — настоящий запрос генерации (с ответом в один токен).
    /// На круг мониторинга он не приходится — определённый ответ каталог держит четверть часа, а
    /// неопределённый три минуты, что заведомо длиннее 45-секундного круга. Срок кэша короче круга
    /// означал бы запрос на каждом круге, то есть беспрерывный стук в поставщика ровно тогда, когда
    /// он и так отказывает.
    /// </summary>
    private static async Task<string?> ModelDetailAsync(IServiceProvider sp, string name, IntegrationEngine cfg, CancellationToken ct)
    {
        var status = await sp.GetRequiredService<IRecognitionModelCatalog>()
            .GetStatusAsync(name, cfg, cfg.Model ?? string.Empty, ct: ct);
        var issue = EngineReadiness.ModelIssue(name, cfg, status);
        // Через исключение — потому что «нездоров» в этом мониторинге выражается только так (CheckAsync).
        if (issue is not null) throw new InvalidOperationException(char.ToUpperInvariant(issue[0]) + issue[1..]);
        return null;
    }

    private async Task<string?> CheckOllamaAsync(string? baseUrl, CancellationToken ct)
    {
        var url = (string.IsNullOrWhiteSpace(baseUrl) ? "http://localhost:11434" : baseUrl).TrimEnd('/') + "/api/tags";
        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(5);
        var resp = await http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Ollama ответил {(int)resp.StatusCode}");
        return null;
    }

    private async Task<string?> CheckGeminiAsync(string apiKey, string? model, CancellationToken ct)
    {
        var m = string.IsNullOrWhiteSpace(model) ? RecognitionDefaults.GeminiModel : model;
        // Ключ заголовком, а не в строке запроса — см. GeminiRecognizerEngine: URL уходит в тексты
        // исключений и в логи прокси, а сюда мы ходим по расписанию, то есть постоянно.
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{m}";
        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(8);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
        var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gemini ответил {(int)resp.StatusCode}");
        return null;
    }

    private static string Short(string s) => s.Length <= 200 ? s : s[..200];
}
