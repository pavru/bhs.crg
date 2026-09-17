using System.Text;
using System.Collections.Concurrent;
using System.Text.Json;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Recognition;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Settings;

/// <summary>
/// Спрашивает у движка распознавания, принимает ли он назначенную ему модель (issue #799).
/// Смысл проверки и почему она устроена именно так — в <see cref="IRecognitionModelCatalog" />.
/// </summary>
public class RecognitionModelCatalog(
    HttpClient http, IMemoryCache cache, ILogger<RecognitionModelCatalog> logger,
    IEnumerable<IRecognizerEngine> engines
) : IRecognitionModelCatalog
{
    /// <summary>
    /// «Поставщик модель принимает» — полсуток: каталог моделей меняется раз в месяцы. Пересмотр раньше
    /// срока — дело того, кто за него платит (<see cref="ModelProbe.Refresh"/>), а не этого числа.
    /// </summary>
    private static readonly TimeSpan OkTtl = TimeSpan.FromHours(12);

    /// <summary>
    /// «Не проверено» — минута. Это не вердикт, а его отсутствие (сеть, деньги, лимит), и держать его
    /// долго значило бы показывать «не проверено» там, где поставщик уже ответил бы.
    ///
    /// Срок НЕ подгоняется под чьё-либо расписание (issue #921): частотой платной пробы владеет тот,
    /// кто за неё платит, выбирая <see cref="ModelProbe"/>. Срок, подогнанный под чужой круг, делал бы
    /// каталог молчаливым участником чужого расписания — и менять одно без другого стало бы нельзя.
    /// </summary>
    private static readonly TimeSpan UnknownTtl = TimeSpan.FromMinutes(1);

    /// <summary>
    /// «Модель видит картинку» живёт час: свойство это у пары (модель, сборка Ollama) постоянное, а
    /// смена любой из них меняет ключ кэша и без срока. Час — не про экономию секунд, а про то, что
    /// распознавание вызывается постранично: без кэша альбом в двести листов оплатил бы двести проб.
    /// </summary>
    private static readonly TimeSpan SightedTtl = TimeSpan.FromHours(1);

    /// <summary>
    /// «Модель слепа» — четверть часа. Короче, чем у зрячей, намеренно: вердикт запрещает работу, и
    /// человек, обновивший Ollama, должен увидеть это в обозримое время, а не через час.
    /// </summary>
    private static readonly TimeSpan BlindTtl = TimeSpan.FromMinutes(15);

    /// <summary>
    /// «Канарейка ничего не выяснила» — двадцать минут, и это НЕ та же величина, что у облачной
    /// пробы, хотя случай на вид тот же: повтор канарейки стоит до полутора минут ВНУТРИ распознавания
    /// страницы. Модель, отвечающая на канарейку молчанием (замер 2026-08-20: 196 с и пустота),
    /// при трёхминутном сроке съедала бы прогон альбома пробой, которой заведомо нечего выяснить.
    /// </summary>
    private static readonly TimeSpan CanaryUnknownTtl = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Срок ожидания канарейки. Ответ на неё — секунды (замер: 6 с на трёхполосной картинке,
    /// 13 с с полным промптом штампа), но ПЕРВЫЙ вызов после простоя грузит модель с диска в память,
    /// и это уже минуты. Полторы минуты — компромисс: холодный старт укладывается, а страница
    /// настроек не висит пять минут, как позволял бы клиент движка.
    /// </summary>
    private static readonly TimeSpan CanaryTimeout = TimeSpan.FromMinutes(1.5);

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
    /// обычный: определённый ответ живёт долго, а сама страница к этому моменту уже отрисована — ждёт
    /// только выпадающий список моделей.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);

    /// <summary>Срок ответа локальной Ollama: она на этой же машине, ждать её 12 секунд незачем.</summary>
    private static readonly TimeSpan LocalTimeout = TimeSpan.FromSeconds(5);

    public async Task<IReadOnlyList<string>?> GetInstalledAsync(string engine, IntegrationEngine cfg, CancellationToken ct = default)
        => (await InstalledEntriesAsync(engine, cfg, ct))?.Select(e => e.Name).ToArray();

    /// <summary>Модель Ollama так, как её отдаёт <c>/api/tags</c>: имя и дайджест весов.</summary>
    private record InstalledModel(string Name, string? Digest);

    /// <summary>
    /// Установленные модели с дайджестами. Дайджест нужен канарейке: имя модели при перекачке
    /// (<c>ollama pull</c> той же версии) не меняется, а веса и поведение — могут, и вердикт о зрении
    /// должен тогда протухнуть сам. Ключ кэша общий с <see cref="GetInstalledAsync" /> — список
    /// один, спрашивается один раз.
    /// </summary>
    private async Task<IReadOnlyList<InstalledModel>?> InstalledEntriesAsync(string engine, IntegrationEngine cfg, CancellationToken ct)
    {
        if (!engine.Equals("Ollama", StringComparison.OrdinalIgnoreCase)) return null;
        return await Cached<IReadOnlyList<InstalledModel>?>($"installed:{cfg.BaseUrl}", ct, fallback: null,
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
                    .Select(e => new InstalledModel(
                        e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        e.TryGetProperty("digest", out var d) ? d.GetString() : null))
                    .Where(e => !string.IsNullOrWhiteSpace(e.Name)).ToArray();
            });
    }

    public async Task<ModelStatus> GetStatusAsync(string engine, IntegrationEngine cfg, string model,
        ModelProbe probe = ModelProbe.IfUnknown, CancellationToken ct = default)
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
        // а не прежнего вердикта. Заодно «модель снята» уходит вместе со сменой модели или ключа —
        // других способов протухнуть у него нет (см. StatusTtl).
        var cacheKey = StatusKey(engine, cfg, model);
        var known = cache.TryGetValue<ModelStatus>(cacheKey, out var hit) ? hit! : null;
        if (probe == ModelProbe.CacheOnly) return known ?? ModelStatus.Unknown;

        Task<ModelStatus> Load(CancellationToken token) => engine.Equals("Gemini", StringComparison.OrdinalIgnoreCase)
            ? ProbeGeminiAsync(cfg.ApiKey!, model, token)
            : engine.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
                ? ProbeAnthropicAsync(cfg.ApiKey!, model, token)
                : Task.FromResult(ModelStatus.Unknown);

        if (probe == ModelProbe.Refresh)
        {
            cache.Remove(cacheKey);
            var fresh = await Cached(cacheKey, ct, ModelStatus.Unknown, StatusTtl, Load);
            // Пересмотр, на который поставщик не ответил, прежний вердикт НЕ отменяет. Иначе
            // «модель снята» при первом же сбое сети стало бы «не проверено», а проверка, молчащая на
            // «не проверено», объявила бы снятую модель восстановленной (issue #921).
            if (fresh.State != ModelState.Unknown || known is null || known.State == ModelState.Unknown)
                return fresh;
            Remember(cacheKey, known);
            return known;
        }

        return await Cached(cacheKey, ct, ModelStatus.Unknown, StatusTtl, Load);
    }

    private void Remember(string key, ModelStatus status)
    {
        if (StatusTtl(status) is { } lifetime) cache.Set(key, status, lifetime);
        else cache.Set(key, status);
    }

    public async Task<VisionStatus> GetVisionAsync(string engine, IntegrationEngine cfg, string model,
        VisionProbe probe = VisionProbe.IfUnknown, CancellationToken ct = default)
    {
        // Облачные движки канарейку не получают, и это решение, а не пропуск: там модель выбирается
        // из курируемого списка vision-моделей, а незнакомое имя поставщик отвергает вслух (см. #799).
        // Слепота без отказа — свойство конкретной сборки Ollama.
        if (string.IsNullOrWhiteSpace(model) || !engine.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
            return VisionStatus.Unknown;

        var target = engines.FirstOrDefault(e => e.Name.Equals(engine, StringComparison.OrdinalIgnoreCase));
        if (target is null) return VisionStatus.Unknown;

        // Дайджест в ключе: имя модели при перекачке не меняется, а веса могут — тогда прежний
        // вердикт о зрении обязан протухнуть сам, без чьей-либо памяти о том, что его надо сбросить.
        var digest = (await InstalledEntriesAsync(engine, cfg, ct))
            ?.FirstOrDefault(m => EngineReadiness.SameModel(m.Name, model))?.Digest;
        var cacheKey = $"vision:{engine}:{model}:{cfg.BaseUrl}:{digest}";

        // Кэш смотрим ДО обращения к движку — иначе постраничный прогон разошёлся бы мимо кэша по
        // счастью, а не по устройству. Кроме случая, когда человек нажал «проверить заново»: там
        // спрашивают именно потому, что прежнему ответу больше не верят (обновили Ollama, перекачали
        // веса), и отдать ему кэш значило бы сделать единственное средство перепроверки пустышкой.
        if (probe == VisionProbe.Refresh) cache.Remove(cacheKey);
        else if (cache.TryGetValue<VisionStatus>(cacheKey, out var known)) return known!;
        if (probe == VisionProbe.CacheOnly) return VisionStatus.Unknown;

        return await Cached(cacheKey, ct, VisionStatus.Unknown,
            v => v.State switch
            {
                VisionState.Sighted => SightedTtl,
                VisionState.Blind => BlindTtl,
                _ => CanaryUnknownTtl,
            },
            async token =>
            {
                // Свой срок: клиент движка живёт с пятиминутным таймаутом (страница из альбома на
                // CPU считается минутами), но канарейку столько ждать незачем — и страница настроек
                // тем более.
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(CanaryTimeout);
                string raw;
                try
                {
                    // Движок вызывается НАПРЯМУЮ, минуя цепочку (IDocumentRecognizer). Через неё
                    // вышло бы «цепочка → каталог → цепочка»: цепочка спрашивает про зрение, а
                    // канарейка возвращается в неё же. Соблазн реальный — цепочка выглядит
                    // правильным входом в распознавание.
                    raw = await target.RecognizeRawAsync(
                        VisionCanary.Png, VisionCanary.MimeType, VisionCanary.Fields, VisionCanary.BuildPrompt, deadline.Token);
                }
                catch (Exception ex) when (ex is RecognitionUnavailableException or RecognitionLimitException)
                {
                    // Движок не ответил — это «не проверили», а не приговор модели.
                    logger.LogInformation("Канарейка зрения {Engine}/{Model}: движок не ответил — {Message}", engine, model, ex.Message);
                    return VisionStatus.Unknown;
                }

                if (VisionCanary.SeesImage(raw))
                {
                    logger.LogInformation("Канарейка зрения {Engine}/{Model}: модель видит изображение", engine, model);
                    return VisionStatus.Sighted;
                }

                // Пустой ответ слепотой НЕ считаем, хотя соблазн есть: замер 2026-08-20 показал, что
                // модель, не получившая картинку, может уйти в размышления на три минуты и вернуть
                // пустоту. Отсюда цена ошибки: «слепа» запрещает работу, и назначить этот вердикт по
                // молчанию значило бы отключать движок за медлительность. Ограничение известное —
                // слепоту такой модели канарейка не увидит, её ловит уже разбор ответа (issue #803).
                if (string.IsNullOrWhiteSpace(raw))
                {
                    logger.LogInformation("Канарейка зрения {Engine}/{Model}: пустой ответ — считаем непроверенной", engine, model);
                    return VisionStatus.Unknown;
                }

                logger.LogWarning("Канарейка зрения {Engine}/{Model}: модель ответила, но цветов не назвала — {Excerpt}",
                    engine, model, VisionCanary.Excerpt(raw));
                return new VisionStatus(VisionState.Blind, VisionCanary.Excerpt(raw));
            });
    }

    /// <summary>
    /// Проба генерацией: один токен на выходе. Ключ уходит заголовком, а не в строке запроса — URL
    /// попадает в логи и в тексты исключений (та же причина, что у <c>GeminiRecognizerEngine</c>).
    /// </summary>
    /// <summary>
    /// Срок вердикта по его виду. <c>null</c> — бессрочно: «модель снята» не должно само становиться
    /// «не проверено», иначе проверка, которая на «не проверено» молчит, объявила бы снятую модель
    /// восстановленной (issue #921). Открыт ради теста: ошибка здесь не видна ни на одном экране.
    /// </summary>
    public static TimeSpan? StatusTtl(ModelStatus status) => status.State switch
    {
        ModelState.Gone => null,
        ModelState.Ok => OkTtl,
        _ => UnknownTtl,
    };

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
    /// Отправить пробу и прочитать ответ. «Нет такой модели» — ТОЛЬКО 404. Всё остальное (кончились
    /// деньги, превышен лимит, ключ отозван, сеть молчит) — «не проверено»: объявить модель
    /// несуществующей из-за пустого счёта значит отправить пользователя менять то, что работает.
    ///
    /// Пробы к разным поставщикам идут параллельно. Раньше их выстраивали в очередь: на машине с
    /// частично недостижимым IPv6 холодное соединение обходилось в секунды, и параллельные пробы
    /// срывались в таймаут. Очередь это лечила лишь тем, что следующая проба шла по уже открытому
    /// соединению. С #918 адреса пробуются внахлёст, и очередь снята (issue #925) — по замеру на такой
    /// машине, холодные соединения, 12 раундов, Gemini и Anthropic одновременно: штатное подключение
    /// срывалось в таймаут 4 раза из 24 разом и 3 из 24 по одной (то есть очередь не спасала и тогда),
    /// подключение внахлёст — 0 из 48, худший ответ 794 мс в обоих режимах. От двойной оплаты
    /// защищает общая проверка на ключ в <see cref="Cached{T}"/>, а не очередь.
    /// </summary>
    private async Task<ModelStatus> ProbeAsync(string engine, string model, HttpRequestMessage req, CancellationToken ct)
    {
        // Каждая проба здесь оплачивается — пишем о каждой. Без этого число платных запросов
        // проверяется только по счёту у поставщика (issue #921).
        logger.LogInformation("Платная проба модели {Engine}/{Model}", engine, model);
        using var resp = await http.SendAsync(req, ct);
        if (resp.IsSuccessStatusCode) return ModelStatus.Ok;
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!ModelGone.Is(resp.StatusCode))
        {
            logger.LogInformation("Проверка модели {Engine}/{Model}: {Status} — считаем непроверенной", engine, model, (int)resp.StatusCode);
            return ModelStatus.Unknown;
        }
        logger.LogInformation("Модель {Engine}/{Model} больше не обслуживается: {Body}", engine, model, Short(body));
        return new ModelStatus(ModelState.Gone, ModelGone.AdviceFrom(body));
    }

    public void ObserveGone(string engine, IntegrationEngine cfg, string model, string? advice)
    {
        // Только облачные движки и только под ключом, под которым вердикт и читают: у Ollama «снята»
        // определяется списком установленных, а вердикт под пустым ключом не прочтёт никто.
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(cfg.ApiKey)) return;
        if (!engine.Equals("Gemini", StringComparison.OrdinalIgnoreCase)
            && !engine.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)) return;

        logger.LogWarning("Модель {Engine}/{Model} больше не обслуживается — по отказу распознавания", engine, model);
        Remember(StatusKey(engine, cfg, model), new ModelStatus(ModelState.Gone, advice));
    }

    private static string StatusKey(string engine, IntegrationEngine cfg, string model)
        => $"status:{engine}:{model}:{cfg.ApiKey!.GetHashCode(StringComparison.Ordinal)}";

    /// <summary>
    /// Выполняющиеся проверки — по одной на ключ кэша на весь процесс (issue #924). Статическое поле,
    /// потому что каталог живёт в области запроса, а мониторинг и экран настроек — в разных областях:
    /// поле экземпляра ничего бы не склеило.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Lazy<Task<object?>>> InFlight = new();

    /// <summary>
    /// Кэш с разным сроком для определённого и неопределённого ответа. Сбой любого рода — это
    /// «не проверено» (см. описание <see cref="IRecognitionModelCatalog" />), поэтому исключение
    /// гасится здесь, одним местом на все способы проверки.
    ///
    /// Одновременные промахи по одному ключу ждут ОДНУ проверку (issue #924). Раньше каждый шёл к
    /// поставщику сам, и оплачивались оба запроса: мониторинг на своём круге и открытие настроек,
    /// две вкладки настроек. Очередь проб, стоявшая тогда перед запросом, этого не предотвращала — она
    /// только выстраивала запросы, и второй, дождавшись первого, отправлял свой.
    ///
    /// Проверка запускается без токена вызывающего, а каждый ждёт её со своим: иначе ушедший со
    /// страницы отменил бы уже оплаченную пробу для всех, кто ждёт того же ответа. Бесконечной она
    /// от этого не становится — у каждого способа проверки свой срок (клиент каталога, срок Ollama,
    /// срок канарейки).
    /// </summary>
    private async Task<T> Cached<T>(string key, CancellationToken ct, T fallback, Func<T, TimeSpan?> ttl, Func<CancellationToken, Task<T>> load)
    {
        if (cache.TryGetValue<T>(key, out var hit)) return hit!;
        var flight = InFlight.GetOrAdd(key, _ => new Lazy<Task<object?>>(() => LoadAndStoreAsync(key, fallback, ttl, load)));
        return (T)(await flight.Value.WaitAsync(ct))!;
    }

    private async Task<object?> LoadAndStoreAsync<T>(string key, T fallback, Func<T, TimeSpan?> ttl, Func<CancellationToken, Task<T>> load)
    {
        try
        {
            T value;
            try
            {
                value = await load(CancellationToken.None);
            }
            // Отменить нас может только собственный срок проверки — то есть это тоже «не проверено».
            catch (Exception ex)
            {
                logger.LogInformation("Проверка модели ({Key}) не удалась: {Message}", key, ex.Message);
                value = fallback;
            }
            // Проба, ушедшая до того, как распознавание наблюдало 404 (ObserveGone), и вернувшаяся без
            // ответа, наблюдённый вердикт не затирает — то же правило, что у Refresh (issue #923).
            if (value is ModelStatus { State: ModelState.Unknown }
                && cache.TryGetValue<ModelStatus>(key, out var observed) && observed!.State == ModelState.Gone)
                return observed;
            if (ttl(value) is { } lifetime) cache.Set(key, value, lifetime);
            else cache.Set(key, value);
            return value;
        }
        finally
        {
            // Снимаем ПОСЛЕ записи в кэш: пришедший в промежутке найдёт либо эту проверку, либо ответ.
            InFlight.TryRemove(key, out _);
        }
    }

    private static string Short(string s) => s.Length <= 300 ? s : s[..300];
}
