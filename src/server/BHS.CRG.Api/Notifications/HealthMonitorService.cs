using BHS.CRG.Application.Notifications;
using BHS.CRG.Application.Settings;
using BHS.CRG.Domain.Notifications;
using BHS.CRG.Infrastructure.Http;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Storage;
using BHS.CRG.Infrastructure.Updates;
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

    // Объявленное прошлым процессом ещё не прочитано. Читаем на тике, а не на старте службы: база
    // могла не отвечать, и тогда пробуем на следующем круге (issue #920).
    private bool _restored;

    // Когда пробу модели последний раз разрешали и с какой конфигурацией (issue #921). В памяти
    // намеренно: после перезапуска вердикта в кэше каталога всё равно нет, и первая проба нужна.
    private readonly Dictionary<string, DateTimeOffset> _probePaidAt = [];
    private readonly Dictionary<string, string> _probeConfig = [];

    // Что лежит в базе сейчас — чтобы писать только при изменении, а не каждые 45 секунд.
    private IReadOnlyDictionary<string, HealthState> _saved = new Dictionary<string, HealthState>();

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

        // Прокси — отдельной строкой, когда он задан и им кто-то пользуется (issue #937). Без неё
        // упавший прокси приходил бы пачкой одинаковых уведомлений «поставщик недоступен»: по одному
        // на каждый сервис с галкой, и ни одно не называло бы настоящую причину.
        var proxyState = sp.GetRequiredService<OutboundProxyState>();
        var viaProxy = proxyState.InUse.ToHashSet();
        Probe? proxyProbe = null;
        if (viaProxy.Count > 0)
        {
            proxyProbe = await ProbeAsync("proxy", "Прокси", HealthClass.Engine, () => CheckProxyAsync(settings.Proxy, ct));
            probes.Add(proxyProbe);
        }

        // Пока прокси не отвечает, сервисы за ним не проверяем вовсе: проба всё равно скажет только
        // то, что уже сказано строкой выше, — а на облачном движке она ещё и стоит запроса.
        var paused = new List<(string Code, string Name)>();
        bool Skip(OutboundService service, string code, string name)
        {
            if (proxyProbe is not { Ok: false } || !viaProxy.Contains(service)) return false;
            paused.Add((code, name));
            return true;
        }

        // Движки распознавания проверяем ровно те, что РЕАЛЬНО участвуют в работе, — тем же
        // правилом, что и цепочка (EngineReadiness, issue #797). Раньше условия были свои: Ollama
        // проверялась при одной галке «включён», и без выбранной модели мониторинг сообщал бы о
        // недоступности движка, которым система всё равно не пользуется.
        var ollama = settings.Rec("Ollama");
        if (EngineReadiness.IsUsableForRecognition("Ollama", ollama)
            && !Skip(OutboundService.Ollama, "recognition.ollama", "Ollama (распознавание)"))
            probes.Add(await ProbeAsync("recognition.ollama", "Ollama (распознавание)", HealthClass.Engine, async () =>
            {
                await CheckOllamaAsync(ollama.BaseUrl, ct);
                return await ModelDetailAsync(sp, "recognition.ollama", "Ollama", ollama, ct);
            }, OutboundService.Ollama, proxyState));

        // Gemini — сначала лёгкий GET метаданных (доступен ли движок вообще), потом проверка самой
        // модели. Вторая стоит одного запроса генерации на ответ (см. ModelDetailAsync): метаданные
        // про снятую с обслуживания модель молчат, и без пробы мониторинг её не увидит.
        var gemini = settings.Rec("Gemini");
        if (EngineReadiness.IsUsableForRecognition("Gemini", gemini)
            && !Skip(OutboundService.Gemini, "recognition.gemini", "Gemini (распознавание)"))
            probes.Add(await ProbeAsync("recognition.gemini", "Gemini (распознавание)", HealthClass.Engine, async () =>
            {
                await CheckGeminiAsync(gemini.ApiKey!, gemini.Model, ct);
                return await ModelDetailAsync(sp, "recognition.gemini", "Gemini", gemini, ct);
            }, OutboundService.Gemini, proxyState));

        var store = sp.GetRequiredService<ServiceStateStore>();
        await RestoreAnnouncedAsync(store, ct);

        // Компоненты, которых больше не проверяем, забываем: иначе выключенный и снова включённый
        // движок унаследовал бы счётчики прошлой жизни.
        // Приостановленные — тоже «наши»: забыв их, мы обнулили бы счётчики и объявленное состояние
        // за время, пока прокси лежит, и вернувшийся движок отчитался бы «восстановлен» о том, о чём
        // не объявляли.
        var alive = probes.Select(p => p.Code).Concat(paused.Select(p => p.Code)).ToList();
        _hysteresis.Retain(alive);
        ForgetProbeSchedule(alive);

        var notifier = sp.GetRequiredService<INotificationService>();
        var snapshot = new List<ComponentHealth>(probes.Count);
        foreach (var probe in probes)
        {
            // Объявление и снимок берутся из гистерезиса, а не из пробы: одна неудача внешнего
            // движка — ещё не отказ (issue #917).
            var move = _hysteresis.Observe(probe.Code, probe.Class, probe.Ok);
            snapshot.Add(new ComponentHealth(probe.Code, probe.Name, probe.Class,
                _hysteresis.StateOf(probe.Code, probe.Ok), probe.Detail, DateTimeOffset.UtcNow)
            {
                ViaProxy = probe.Service is { } via && viaProxy.Contains(via),
            });

            if (move == HealthTransition.WentDown)
                await notifier.PublishAsync(SeverityFor(probe.Class), $"{probe.Name}: недоступен",
                    Explain(probe), "Состояние системы", ct: ct);
            else if (move == HealthTransition.CameUp)
                await notifier.PublishAsync(NotificationSeverity.Info, $"{probe.Name}: восстановлен",
                    "Компонент снова доступен.", "Состояние системы", ct: ct);
        }

        // Приостановленные — в снимке, но без состояния: их не проверяли. Пропасть из панели они не
        // могут — исчезнувшая строка читается как «выключено», а сервис включён и ждёт прокси.
        foreach (var (code, name) in paused)
            snapshot.Add(new ComponentHealth(code, name, HealthClass.Engine, HealthState.Unknown,
                "Не проверяли: прокси недоступен.", DateTimeOffset.UtcNow) { ViaProxy = true });

        // Снимок присваивается ПОСЛЕ разбора: до него состояние ещё не подтверждено.
        _snapshot = snapshot;

        await SaveAnnouncedAsync(store, ct);
    }

    /// <summary>
    /// Возвращает объявленное прошлым процессом — до разбора первых проб, иначе уже объявленный
    /// отказ объявлялся бы заново. Не удалось прочитать — продолжаем с чистого листа и пробуем на
    /// следующем круге: мониторинг, который молчит из-за своей же истории, хуже повторного
    /// уведомления.
    /// </summary>
    private async Task RestoreAnnouncedAsync(ServiceStateStore store, CancellationToken ct)
    {
        if (_restored) return;
        try
        {
            var announced = (await store.LoadAsync<HealthAnnouncedState>(HealthAnnouncedState.Key, ct)).ToStates();
            _hysteresis.Restore(announced);
            _saved = announced;
            _restored = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Не удалось прочитать объявленное состояние компонентов — повторим на следующей проверке");
        }
    }

    /// <summary>
    /// Пишет объявленное, когда оно изменилось. Сбой записи не роняет круг: значение останется
    /// «несохранённым» и уйдёт на следующем — а база сама может быть тем, что сейчас не отвечает.
    /// </summary>
    private async Task SaveAnnouncedAsync(ServiceStateStore store, CancellationToken ct)
    {
        // Пока прошлое не прочитано, писать нельзя: затёрли бы его своим, ещё неполным.
        if (!_restored) return;
        var announced = _hysteresis.Announced;
        if (SameAnnouncement(announced, _saved)) return;
        try
        {
            await store.SaveAsync(HealthAnnouncedState.Key, HealthAnnouncedState.From(announced), ct);
            _saved = announced;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Не удалось сохранить объявленное состояние компонентов — повторим на следующей проверке");
        }
    }

    private static bool SameAnnouncement(IReadOnlyDictionary<string, HealthState> a, IReadOnlyDictionary<string, HealthState> b)
        => a.Count == b.Count && a.All(x => b.TryGetValue(x.Key, out var v) && v == x.Value);

    /// <summary>Результат одной пробы — сырой, до подтверждения сериями.</summary>
    /// <param name="Service">Чей это путь наружу; <c>null</c> — своё, наружу не ходит.</param>
    private sealed record Probe(string Code, string Name, HealthClass Class, bool Ok, string? Detail,
        OutboundService? Service = null);

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

    /// <param name="service">Чей путь наружу проверяем — чтобы отказ разбирал общий классификатор
    /// (issue #937): за прокси «движок недоступен» чаще всего означает беду не с движком.</param>
    private static async Task<Probe> ProbeAsync(string code, string name, HealthClass @class, Func<Task<string?>> probe,
        OutboundService? service = null, OutboundProxyState? proxy = null)
    {
        try
        {
            return new Probe(code, name, @class, true, await probe(), service);
        }
        // Остановка приложения — не отказ компонента: иначе последний тик объявлял бы недоступным
        // то, что просто не успело ответить перед выключением.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var detail = service is { } s && proxy is not null
                ? OutboundDiagnosis.Describe(ex, s, proxy)
                : OutboundDiagnosis.Mask(ex.Message);
            return new Probe(code, name, @class, false, Short(detail), service);
        }
    }

    /// <summary>
    /// Прокси проверяем СОКЕТОМ, а не туннелем до сервиса: круг идёт каждые 45 секунд, и туннель
    /// означал бы постоянный стук в чужой сервис без нужды. Отказ прокси в авторизации или в цели
    /// увидит проба самого сервиса — и назовёт его тем же классификатором.
    /// </summary>
    private static async Task<string?> CheckProxyAsync(ProxySettings proxy, CancellationToken ct)
    {
        var result = await ProxyCheck.RunAsync(proxy, null, null, ct, TimeSpan.FromSeconds(5));
        if (!result.Ok) throw new InvalidOperationException(result.Message);
        return null;
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
    /// Когда платить, решает <see cref="ModelProbeSchedule"/>, а не срок кэша каталога (issue #921):
    /// на остальных кругах вердикт берётся из кэша. Ollama отвечает по списку установленных моделей,
    /// и режим пробы её не касается — расписание к ней применяется, ничего не меняя.
    /// </summary>
    private async Task<string?> ModelDetailAsync(IServiceProvider sp, string code, string name, IntegrationEngine cfg, CancellationToken ct)
    {
        var config = $"{cfg.Model}|{cfg.ApiKey?.GetHashCode(StringComparison.Ordinal)}";
        var paidAt = _probePaidAt.TryGetValue(code, out var at) ? at : (DateTimeOffset?)null;
        var configChanged = _probeConfig.TryGetValue(code, out var seen) && seen != config;
        var announcedDown = _hysteresis.Announced.TryGetValue(code, out var announced) && announced == HealthState.Down;

        var now = DateTimeOffset.UtcNow;
        var mode = ModelProbeSchedule.Decide(paidAt, configChanged, announcedDown, now);
        if (mode != ModelProbe.CacheOnly)
        {
            _probePaidAt[code] = now;
            _probeConfig[code] = config;
        }

        var status = await sp.GetRequiredService<IRecognitionModelCatalog>()
            .GetStatusAsync(name, cfg, cfg.Model ?? string.Empty, mode, ct);
        var issue = EngineReadiness.ModelIssue(name, cfg, status);
        // Через исключение — потому что «нездоров» в этом мониторинге выражается только так (CheckAsync).
        if (issue is not null) throw new InvalidOperationException(char.ToUpperInvariant(issue[0]) + issue[1..]);
        return null;
    }

    /// <summary>Расписание пробы для компонентов, которых больше не проверяем, забывается: включённый заново движок начинает с пробы.</summary>
    private void ForgetProbeSchedule(IEnumerable<string> codes)
    {
        var keep = codes.ToHashSet();
        foreach (var code in _probePaidAt.Keys.Where(c => !keep.Contains(c)).ToList()) _probePaidAt.Remove(code);
        foreach (var code in _probeConfig.Keys.Where(c => !keep.Contains(c)).ToList()) _probeConfig.Remove(code);
    }

    private async Task<string?> CheckOllamaAsync(string? baseUrl, CancellationToken ct)
    {
        var url = (string.IsNullOrWhiteSpace(baseUrl) ? "http://localhost:11434" : baseUrl).TrimEnd('/') + "/api/tags";
        using var http = httpFactory.CreateClient(OutboundProxy.ClientName(ClientPurpose, OutboundService.Ollama));
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
        using var http = httpFactory.CreateClient(OutboundProxy.ClientName(ClientPurpose, OutboundService.Gemini));
        http.Timeout = TimeSpan.FromSeconds(8);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
        var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gemini ответил {(int)resp.StatusCode}");
        return null;
    }

    /// <summary>Назначение в имени клиентов проб: у каждой пробы клиент своего сервиса, чтобы проба
    /// шла тем же путём, что и работа, — через прокси, если у сервиса стоит галка (issue #936).</summary>
    public const string ClientPurpose = "health";

    private static string Short(string s) => s.Length <= 200 ? s : s[..200];
}
