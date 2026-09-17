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
/// Периодически проверяет состояние БД, объектного хранилища и (если включён) Ollama.
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
    private readonly HealthHysteresis _hysteresis = new();

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

        var probes = new List<Probe>
        {
            await ProbeAsync("db", "База данных", HealthClass.Core, () => CheckPostgresAsync(sp, ct)),
            await ProbeAsync("storage", "Хранилище", HealthClass.Core, () => CheckStorageAsync(ct)),
        };

        // Движки распознавания проверяем ровно те, что РЕАЛЬНО участвуют в работе, — тем же
        // правилом, что и цепочка (EngineReadiness, issue #797). Раньше условия были свои: Ollama
        // проверялась при одной галке «включён», и без выбранной модели мониторинг сообщал бы о
        // недоступности движка, которым система всё равно не пользуется.
        var ollama = settings.Rec("Ollama");
        if (EngineReadiness.IsUsableForRecognition("Ollama", ollama))
            probes.Add(await ProbeAsync("recognition.ollama", "Ollama (распознавание)", HealthClass.Engine, async () =>
            {
                await CheckOllamaAsync(ollama.BaseUrl, ct);
                return await ModelDetailAsync(sp, "Ollama", ollama, ct);
            }));

        // Gemini — сначала лёгкий GET метаданных (доступен ли движок вообще), потом проверка самой
        // модели. Вторая стоит одного запроса генерации на ответ (см. ModelDetailAsync): метаданные
        // про снятую с обслуживания модель молчат, и без пробы мониторинг её не увидит.
        var gemini = settings.Rec("Gemini");
        if (EngineReadiness.IsUsableForRecognition("Gemini", gemini))
            probes.Add(await ProbeAsync("recognition.gemini", "Gemini (распознавание)", HealthClass.Engine, async () =>
            {
                await CheckGeminiAsync(gemini.ApiKey!, gemini.Model, ct);
                return await ModelDetailAsync(sp, "Gemini", gemini, ct);
            }));

        // Компоненты, которых больше не проверяем, забываем: иначе выключенный и снова включённый
        // движок унаследовал бы счётчики прошлой жизни.
        _hysteresis.Retain(probes.Select(p => p.Code));

        var notifier = sp.GetRequiredService<INotificationService>();
        var snapshot = new List<ComponentHealth>(probes.Count);
        foreach (var probe in probes)
        {
            // Объявление и снимок берутся из гистерезиса, а не из пробы: одна неудача внешнего
            // движка — ещё не отказ (issue #917).
            var move = _hysteresis.Observe(probe.Code, probe.Class, probe.Ok);
            snapshot.Add(new ComponentHealth(probe.Code, probe.Name, probe.Class,
                _hysteresis.StateOf(probe.Code, probe.Ok), probe.Detail, DateTimeOffset.UtcNow));

            if (move == HealthTransition.WentDown)
                await notifier.PublishAsync(SeverityFor(probe.Class), $"{probe.Name}: недоступен",
                    Explain(probe), "Состояние системы", ct: ct);
            else if (move == HealthTransition.CameUp)
                await notifier.PublishAsync(NotificationSeverity.Info, $"{probe.Name}: восстановлен",
                    "Компонент снова доступен.", "Состояние системы", ct: ct);
        }

        // Снимок присваивается ПОСЛЕ разбора: до него состояние ещё не подтверждено.
        _snapshot = snapshot;
    }

    /// <summary>Результат одной пробы — сырой, до подтверждения сериями.</summary>
    private sealed record Probe(string Code, string Name, HealthClass Class, bool Ok, string? Detail);

    // Движки распознавания → Предупреждение; ядро (БД/хранилище) → Ошибка. По классу компонента, а
    // не по началу отображаемого имени: переименование иначе меняло бы строгость молча.
    private static NotificationSeverity SeverityFor(HealthClass @class)
        => @class == HealthClass.Core ? NotificationSeverity.Error : NotificationSeverity.Warning;

    /// <summary>
    /// Порог назван в тексте нарочно: иначе из уведомления не видно, что отказ подтверждался серией,
    /// и разбор обращения уйдёт искать единственную неудачную пробу.
    /// </summary>
    private static string Explain(Probe probe)
    {
        var detail = probe.Detail ?? "Компонент не отвечает.";
        var (down, _) = HealthHysteresis.Thresholds(probe.Class);
        return down > 1 ? $"{detail} Не отвечает {down} проверки подряд." : detail;
    }

    private static async Task<Probe> ProbeAsync(string code, string name, HealthClass @class, Func<Task<string?>> probe)
    {
        try
        {
            return new Probe(code, name, @class, true, await probe());
        }
        // Остановка приложения — не отказ компонента: иначе последний тик объявлял бы недоступным
        // то, что просто не успело ответить перед выключением.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new Probe(code, name, @class, false, Short(ex.Message));
        }
    }

    private static async Task<string?> CheckPostgresAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<AppDbContext>();
        if (!await db.Database.CanConnectAsync(ct))
            throw new InvalidOperationException("Нет соединения с PostgreSQL");
        return null;
    }

    private async Task<string?> CheckStorageAsync(CancellationToken ct)
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
