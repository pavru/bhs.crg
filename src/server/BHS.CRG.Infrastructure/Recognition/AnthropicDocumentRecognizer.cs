using System.Net;
using System.Text;
using System.Text.Json;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Http;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>Движок распознавания через Anthropic Claude (vision). Настройки — из IIntegrationSettings.</summary>
public class AnthropicRecognizerEngine(
    HttpClient http, IIntegrationSettings settings, ILogger<AnthropicRecognizerEngine> logger
) : IRecognizerEngine
{
    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    /// <summary>Срок ответа. Здесь же, чтобы сообщение о таймауте называло настоящее число (см. Gemini).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Потолок ответа. Прежние 2048 были достаточны ровно для штампа: табличный промпт просит массив
    /// строк, и спецификация на сотню позиций не помещалась НИКОГДА — ответ обрывался на середине,
    /// JSON не закрывался, разбор падал, и наверх уходил успешный ноль полей (issue #802).
    ///
    /// Число — расчёт, а не замер: строка таблицы это 40–60 токенов, сотня строк — около шести тысяч;
    /// 16 384 берём с запасом почти втрое. Платят за выданные токены, а не за разрешённые, поэтому
    /// запас ничего не стоит, пока модель им не пользуется. Ошибиться в обе стороны теперь ГРОМКО:
    /// мало — <c>stop_reason: max_tokens</c> и отказ, много — 400 от поставщика.
    /// </summary>
    public const int MaxOutputTokens = 16384;

    public string Name => "Anthropic";

    public async Task<string> RecognizeRawAsync(byte[] file, string mimeType, IReadOnlyList<RecognitionField> fields,
        Func<IReadOnlyList<RecognitionField>, string>? promptBuilder = null, CancellationToken ct = default)
    {
        var cfg = (await settings.GetEffectiveAsync(ct)).Rec("Anthropic");
        var apiKey = cfg.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new RecognitionUnavailableException("Не задан ключ Anthropic.");
        var model = string.IsNullOrWhiteSpace(cfg.Model) ? "claude-sonnet-4-6" : cfg.Model;

        var b64 = Convert.ToBase64String(file);
        object fileBlock = string.Equals(mimeType, "application/pdf", StringComparison.OrdinalIgnoreCase)
            ? new { type = "document", source = new { type = "base64", media_type = "application/pdf", data = b64 } }
            : RecognitionShared.ImageTypes.Contains(mimeType)
                ? new { type = "image", source = new { type = "base64", media_type = RecognitionShared.NormalizeImageMime(mimeType), data = b64 } }
                : throw new RecognitionUnavailableException($"Anthropic: формат не поддерживается: {mimeType}");

        var requestBody = new
        {
            model,
            max_tokens = MaxOutputTokens,
            messages = new object[]
            {
                new { role = "user", content = new object[] { fileBlock, new { type = "text", text = (promptBuilder ?? RecognitionShared.BuildPrompt)(fields) } } },
            },
        };
        var json = JsonSerializer.Serialize(requestBody);

        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            req.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");

            HttpResponseMessage resp;
            string body;
            // Тело читаем здесь же: HttpClient.Timeout отмеряет и чтение контента, а снаружи try
            // таймаут снова стал бы голой отменой, мимо классификации (issue #797).
            try { resp = await http.SendAsync(req, ct); body = await resp.Content.ReadAsStringAsync(ct); }
            catch (Exception ex) when (HttpFailure.IsTimeout(ex, ct))
            {
                // Без ретрая — см. Gemini: повтор докупает только ожидание.
                logger.LogWarning("Anthropic не ответил за {Timeout}", HttpFailure.Format(Timeout));
                throw new RecognitionTimeoutException($"Anthropic: не ответил за {HttpFailure.Format(Timeout)}.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt >= maxAttempts) throw new RecognitionUnavailableException($"Anthropic: ошибка обращения: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct); continue;
            }

            if (resp.IsSuccessStatusCode) return ExtractText(body);

            if (resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode == 529)
            {
                var retryAfter = resp.Headers.RetryAfter?.Delta?.TotalSeconds is { } s ? (int?)s : null;
                if (attempt < maxAttempts) { await Task.Delay(TimeSpan.FromSeconds(retryAfter ?? 5 * attempt), ct); continue; }
                logger.LogWarning("Anthropic лимит/перегрузка: {Status} {Body}", resp.StatusCode, RecognitionShared.Truncate(body, 300));
                throw new RecognitionLimitException("Anthropic: достигнут лимит запросов.", retryAfter);
            }
            if ((int)resp.StatusCode >= 500 && attempt < maxAttempts) { await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct); continue; }

            // 400 «credit balance too low» и пр. — считаем движок недоступным, цепочка перейдёт к следующему
            throw new RecognitionUnavailableException($"Anthropic ответил {(int)resp.StatusCode}: {RecognitionShared.Truncate(body, 300)}");
        }
    }

    /// <summary>
    /// Текст ответа — либо исключение (issue #802). Пустую строку отсюда вернуть НЕЛЬЗЯ: наверху она
    /// неотличима от «модель ответила, что полей нет», и ровно эта неразличимость превращала
    /// обрезанную таблицу в успешный ноль полей.
    ///
    /// Читается и <c>stop_reason</c>: <c>max_tokens</c> значит, что ответ оборвали на середине —
    /// текст в нём есть, он выглядит правдоподобно, и без этой проверки JSON просто не закроется.
    /// </summary>
    private static string ExtractText(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;
        var stop = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;
        if (stop == "max_tokens")
            throw new RecognitionSilentException(
                "Anthropic: ответ не поместился в лимит и оборван на середине — данные пришли неполными.");

        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text" && block.TryGetProperty("text", out var txt))
                {
                    var text = txt.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }

        // Ни одного текстового блока: отказ модели (stop_reason: refusal) или пустой ответ.
        throw new RecognitionSilentException(stop == "refusal"
            ? "Anthropic: модель отказалась отвечать по этому документу."
            : "Anthropic: в ответе нет текста.");
    }
}
