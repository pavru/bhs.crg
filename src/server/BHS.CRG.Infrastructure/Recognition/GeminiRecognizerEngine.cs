using System.Net;
using System.Text;
using System.Text.Json;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Http;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>Движок распознавания через Google Gemini (vision + PDF). Настройки — из IIntegrationSettings.</summary>
public class GeminiRecognizerEngine(
    HttpClient http, IIntegrationSettings settings, ILogger<GeminiRecognizerEngine> logger
) : IRecognizerEngine
{
    /// <summary>
    /// Срок ответа. Задан здесь, а не только при регистрации клиента: движок сам называет его
    /// пользователю в сообщении о таймауте, и разъехавшись, текст врал бы про чужое число.
    /// Облачная модель отвечает секунды; две минуты — запас на большой PDF, а не рабочее время.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Потолок ответа. Задан ЯВНО, хотя поставщик разрешает его не указывать: без него действует
    /// умолчание модели — число, которое меняется вместе с моделью, в коде отсутствует и потому не
    /// может быть названо в тексте отказа. Та же причина, по которой сроки ответа живут константами
    /// на движках (issue #797): движок обязан называть пользователю СВОЁ число.
    ///
    /// Величина — как у Anthropic и по тому же расчёту (см. там). Обрыв по лимиту здесь особенно
    /// тих: мы просим ответ в JSON, и недописанный ответ не разберётся заведомо.
    /// </summary>
    public const int MaxOutputTokens = 16384;

    public string Name => "Gemini";

    public async Task<string> RecognizeRawAsync(byte[] file, string mimeType, IReadOnlyList<RecognitionField> fields,
        Func<IReadOnlyList<RecognitionField>, string>? promptBuilder = null, CancellationToken ct = default)
    {
        var cfg = (await settings.GetEffectiveAsync(ct)).Rec("Gemini");
        var apiKey = cfg.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new RecognitionUnavailableException("Не задан ключ Gemini.");
        var model = string.IsNullOrWhiteSpace(cfg.Model) ? RecognitionDefaults.GeminiModel : cfg.Model;

        var mt = string.Equals(mimeType, "application/pdf", StringComparison.OrdinalIgnoreCase)
            ? "application/pdf"
            : RecognitionShared.ImageTypes.Contains(mimeType) ? RecognitionShared.NormalizeImageMime(mimeType)
            : throw new RecognitionUnavailableException($"Gemini: формат не поддерживается: {mimeType}");

        var requestBody = new
        {
            contents = new object[]
            {
                new { parts = new object[]
                {
                    new { inline_data = new { mime_type = mt, data = Convert.ToBase64String(file) } },
                    new { text = (promptBuilder ?? RecognitionShared.BuildPrompt)(fields) },
                } },
            },
            generationConfig = new
            {
                response_mime_type = "application/json",
                temperature = 0,
                maxOutputTokens = MaxOutputTokens,
            },
        };
        var json = JsonSerializer.Serialize(requestBody);
        // Ключ — заголовком, а не в строке запроса: URL целиком попадает в текст сетевых исключений,
        // в трассировки и в логи любого прокси по пути, и ключ утекал бы вместе с ними.
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";

        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
            HttpResponseMessage resp;
            string body;
            // Тело читаем здесь же: HttpClient.Timeout отмеряет и чтение контента, а снаружи try
            // таймаут снова стал бы голой отменой, мимо классификации (issue #797).
            try { resp = await http.SendAsync(req, ct); body = await resp.Content.ReadAsStringAsync(ct); }
            catch (Exception ex) when (HttpFailure.IsTimeout(ex, ct))
            {
                // Не ретраим: повтор — это ещё столько же ожидания на движке, который уже показал,
                // что не отвечает. Цепочке полезнее сразу перейти к следующему.
                logger.LogWarning("Gemini не ответил за {Timeout}", HttpFailure.Format(Timeout));
                throw new RecognitionTimeoutException($"Gemini: не ответил за {HttpFailure.Format(Timeout)}.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt >= maxAttempts) throw new RecognitionUnavailableException($"Gemini: ошибка обращения: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct); continue;
            }

            if (resp.IsSuccessStatusCode) return ExtractText(body);

            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                if (attempt < maxAttempts) { await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct); continue; }
                logger.LogWarning("Gemini лимит: {Body}", RecognitionShared.Truncate(body, 300));
                throw new RecognitionLimitException("Gemini: достигнут лимит запросов.");
            }
            if ((int)resp.StatusCode >= 500 && attempt < maxAttempts) { await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct); continue; }

            throw new RecognitionUnavailableException($"Gemini ответил {(int)resp.StatusCode}: {RecognitionShared.Truncate(body, 300)}");
        }
    }

    /// <summary>
    /// Текст ответа — либо исключение (issue #802). Пустую строку отсюда вернуть НЕЛЬЗЯ: наверху она
    /// неотличима от «модель ответила, что полей нет».
    ///
    /// Причин промолчать у Gemini несколько, и все они лежат в ответе, который до issue #802 никто
    /// не читал: запрос отклонён фильтром (<c>promptFeedback.blockReason</c>), ответ оборван по
    /// лимиту или фильтру (<c>finishReason</c>). Обрыв по лимиту здесь особенно тих: мы просим
    /// <c>response_mime_type: application/json</c>, поэтому недописанный ответ гарантированно не
    /// разберётся — и раньше уходил как ноль полей.
    /// </summary>
    private static string ExtractText(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("promptFeedback", out var feedback)
            && feedback.TryGetProperty("blockReason", out var blocked) && blocked.GetString() is { } reason)
            throw new RecognitionSilentException($"Gemini: запрос отклонён ({reason}) — ответа нет.");

        if (root.TryGetProperty("candidates", out var cands) && cands.ValueKind == JsonValueKind.Array)
            foreach (var c in cands.EnumerateArray())
            {
                var finish = c.TryGetProperty("finishReason", out var fr) ? fr.GetString() : null;
                if (finish == "MAX_TOKENS")
                    throw new RecognitionSilentException(
                        "Gemini: ответ не поместился в лимит и оборван на середине — данные пришли неполными.");
                if (finish is "SAFETY" or "RECITATION" or "PROHIBITED_CONTENT")
                    throw new RecognitionSilentException($"Gemini: ответ остановлен фильтром ({finish}).");

                if (c.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                {
                    var sb = new StringBuilder();
                    foreach (var p in parts.EnumerateArray())
                        if (p.TryGetProperty("text", out var t)) sb.Append(t.GetString());
                    if (sb.Length > 0) return sb.ToString();
                }
            }

        throw new RecognitionSilentException("Gemini: в ответе нет текста.");
    }
}
