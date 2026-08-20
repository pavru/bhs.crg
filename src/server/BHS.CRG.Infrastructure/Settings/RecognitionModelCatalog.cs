using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BHS.CRG.Application.Settings;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Settings;

/// <summary>
/// Спрашивает у движка распознавания, принимает ли он назначенную ему модель (issue #799).
/// Смысл проверки и почему она устроена именно так — в <see cref="IRecognitionModelCatalog" />.
/// </summary>
public partial class RecognitionModelCatalog(
    HttpClient http, IMemoryCache cache, ILogger<RecognitionModelCatalog> logger
) : IRecognitionModelCatalog
{
    /// <summary>
    /// Определённый ответ облачного поставщика («принимает» / «нет такой») живёт четверть часа:
    /// каталог моделей меняется раз в месяцы, а спрашивают его на каждом открытии настроек.
    /// </summary>
    private static readonly TimeSpan KnownTtl = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Неопределённый ответ облачного поставщика. Три минуты, а НЕ полминуты, и это не про удобство:
    /// пробу шлёт в том числе health-мониторинг раз в 45 секунд, и срок короче его круга означал бы
    /// запрос на каждом круге — то есть беспрерывный стук в поставщика ровно тогда, когда он и так
    /// отказывает (кончились деньги, превышен лимит).
    /// </summary>
    private static readonly TimeSpan CloudRetryTtl = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Список моделей Ollama — на полминуты, каким бы он ни был. Она рядом, спросить её дёшево, а
    /// цена долгого кэша тут прямая: пользователю сказали «скачайте модель», он скачал — и должен
    /// увидеть это сразу, а не через четверть часа.
    /// </summary>
    private static readonly TimeSpan LocalTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Срок ответа облачной пробы (задаётся при регистрации типизированного клиента). Восемь секунд
    /// не хватало: первый запрос к облачному поставщику на холодном соединении занимал ~5 с, и при
    /// двух движках сразу проверка срывалась в таймаут, показывая «не проверено» там, где всё работало.
    ///
    /// Худший случай для списка моделей на странице настроек — сумма: пробы идут по одной, и два
    /// молчащих поставщика подряд дадут около полуминуты ожидания. Случай именно худший, а не
    /// обычный: определённый ответ живёт четверть часа и обновляется по кругу health-мониторинга, а
    /// сама страница к этому моменту уже отрисована — ждёт только выпадающий список моделей.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);

    /// <summary>Срок ответа локальной Ollama: она на этой же машине, ждать её 12 секунд незачем.</summary>
    private static readonly TimeSpan LocalTimeout = TimeSpan.FromSeconds(5);

    public async Task<IReadOnlyList<string>?> GetInstalledAsync(string engine, IntegrationEngine cfg, CancellationToken ct = default)
    {
        if (!engine.Equals("Ollama", StringComparison.OrdinalIgnoreCase)) return null;
        return await Cached<IReadOnlyList<string>?>($"installed:{cfg.BaseUrl}", ct, fallback: null,
            _ => LocalTtl,
            async token =>
            {
                var url = (string.IsNullOrWhiteSpace(cfg.BaseUrl) ? "http://localhost:11434" : cfg.BaseUrl).TrimEnd('/') + "/api/tags";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                // Свой срок поверх общего: клиент один на все проверки, а ждать соседнюю программу
                // столько же, сколько облако за океаном, — это задерживать страницу настроек впустую.
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(LocalTimeout);
                using var resp = await http.SendAsync(req, deadline.Token);
                if (!resp.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(deadline.Token));
                if (!doc.RootElement.TryGetProperty("models", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    return null;   // ответ не той формы — «не проверили», а не «моделей нет»
                return arr.EnumerateArray()
                    .Select(e => e.TryGetProperty("name", out var n) ? n.GetString() : null)
                    .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToArray();
            });
    }

    public async Task<ModelStatus> GetStatusAsync(string engine, IntegrationEngine cfg, string model,
        bool probe = true, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(model)) return ModelStatus.Unknown;

        if (engine.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            var installed = await GetInstalledAsync(engine, cfg, ct);
            if (installed is null) return ModelStatus.Unknown;
            return installed.Any(m => EngineReadiness.SameModel(m, model))
                ? ModelStatus.Ok
                : new ModelStatus(ModelState.Gone, $"скачайте её: ollama pull {model}");
        }

        if (string.IsNullOrWhiteSpace(cfg.ApiKey)) return ModelStatus.Unknown;

        // В ключ кэша входит и ключ доступа: сменив его, пользователь ждёт ответа про НОВЫЙ доступ,
        // а не ещё четверти часа прежнего.
        var cacheKey = $"status:{engine}:{model}:{cfg.ApiKey.GetHashCode(StringComparison.Ordinal)}";
        if (!probe)
            // Спрашивать разрешили только кэш: пробу стоит тратить на ту модель, с которой работают,
            // а не на каждый пункт списка (см. IRecognitionModelCatalog).
            return cache.TryGetValue<ModelStatus>(cacheKey, out var known) ? known! : ModelStatus.Unknown;
        return await Cached(cacheKey, ct, ModelStatus.Unknown,
            s => s.State == ModelState.Unknown ? CloudRetryTtl : KnownTtl,
            token => engine.Equals("Gemini", StringComparison.OrdinalIgnoreCase)
                ? ProbeGeminiAsync(cfg.ApiKey!, model, token)
                : engine.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
                    ? ProbeAnthropicAsync(cfg.ApiKey!, model, token)
                    : Task.FromResult(ModelStatus.Unknown));
    }

    /// <summary>
    /// Проба генерацией: один токен на выходе. Ключ уходит заголовком, а не в строке запроса — URL
    /// попадает в логи и в тексты исключений (та же причина, что у <c>GeminiRecognizerEngine</c>).
    /// </summary>
    private async Task<ModelStatus> ProbeGeminiAsync(string apiKey, string model, CancellationToken ct)
    {
        const string body = @"{""contents"":[{""parts"":[{""text"":""1""}]}],""generationConfig"":{""maxOutputTokens"":1}}";
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
        return await ProbeAsync("Gemini", model, req, ct);
    }

    private async Task<ModelStatus> ProbeAnthropicAsync(string apiKey, string model, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            model,
            max_tokens = 1,
            messages = new[] { new { role = "user", content = "1" } },
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        return await ProbeAsync("Anthropic", model, req, ct);
    }

    /// <summary>
    /// Пробы идут по одной. Запущенные разом, они срывались в таймаут: на машине с нерабочим IPv6
    /// первое соединение с облачным хостом обходится в несколько секунд (сначала AAAA, потом откат на
    /// IPv4), и параллельные пробы этот срок друг другу только удлиняли — до таймаута у всех сразу.
    /// Поодиночке каждая укладывается, а последующие достаются уже установленному соединению.
    /// </summary>
    private static readonly SemaphoreSlim ProbeGate = new(1);

    /// <summary>
    /// Отправить пробу и прочитать ответ. «Нет такой модели» — ТОЛЬКО 404. Всё остальное (кончились
    /// деньги, превышен лимит, ключ отозван, сеть молчит) — «не проверено»: объявить модель
    /// несуществующей из-за пустого счёта значит отправить пользователя менять то, что работает.
    /// </summary>
    private async Task<ModelStatus> ProbeAsync(string engine, string model, HttpRequestMessage req, CancellationToken ct)
    {
        await ProbeGate.WaitAsync(ct);
        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct); } finally { ProbeGate.Release(); }
        using var _ = resp;
        if (resp.IsSuccessStatusCode) return ModelStatus.Ok;
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (resp.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            logger.LogInformation("Проверка модели {Engine}/{Model}: {Status} — считаем непроверенной", engine, model, (int)resp.StatusCode);
            return ModelStatus.Unknown;
        }
        logger.LogInformation("Модель {Engine}/{Model} больше не обслуживается: {Body}", engine, model, Short(body));
        return new ModelStatus(ModelState.Gone, AdviceFrom(body));
    }

    /// <summary>
    /// Совет поставщика из текста отказа (открыт ради теста: разбор чужого сообщения — то, что ломается
    /// молча при смене формулировки). Google в ответе 404 прямо называет замену
    /// («Please update your code to use models/gemini-3.5-flash-lite…») — это самое полезное, что есть
    /// в сообщении, и терять его, оставив сухое «модель недоступна», было бы расточительством.
    /// </summary>
    public static string? AdviceFrom(string body)
    {
        var m = SuggestedModel().Match(body);
        return m.Success ? $"поставщик рекомендует {m.Groups[1].Value}" : null;
    }

    [GeneratedRegex(@"use\s+models/([A-Za-z0-9.\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SuggestedModel();

    /// <summary>
    /// Кэш с разным сроком для определённого и неопределённого ответа. Сбой любого рода — это
    /// «не проверено» (см. описание <see cref="IRecognitionModelCatalog" />), поэтому исключение
    /// гасится здесь, одним местом на все способы проверки.
    /// </summary>
    private async Task<T> Cached<T>(string key, CancellationToken ct, T fallback, Func<T, TimeSpan> ttl, Func<CancellationToken, Task<T>> load)
    {
        if (cache.TryGetValue<T>(key, out var hit)) return hit!;
        T value;
        try
        {
            value = await load(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogInformation("Проверка модели ({Key}) не удалась: {Message}", key, ex.Message);
            value = fallback;
        }
        cache.Set(key, value, ttl(value));
        return value;
    }

    private static string Short(string s) => s.Length <= 300 ? s : s[..300];
}
