using System.Text;
using System.Text.Json;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Settings;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>
/// Локальный движок распознавания через Ollama (vision-модели: qwen2.5vl, llama3.2-vision, minicpm-v).
/// Настройки — из IIntegrationSettings. Принимает изображения; PDF предварительно растеризуется
/// в PNG-страницы (<see cref="PdfRasterizer"/>) без потери качества.
/// </summary>
public class OllamaRecognizerEngine(
    HttpClient http, IIntegrationSettings settings, ILogger<OllamaRecognizerEngine> logger
) : IRecognizerEngine
{
    private const string PdfMime = "application/pdf";

    public string Name => "Ollama";

    public async Task<string> RecognizeRawAsync(byte[] file, string mimeType, IReadOnlyList<RecognitionField> fields,
        Func<IReadOnlyList<RecognitionField>, string>? promptBuilder = null, CancellationToken ct = default)
    {
        var cfg = (await settings.GetEffectiveAsync(ct)).Rec("Ollama");
        var model = cfg.Model;
        if (string.IsNullOrWhiteSpace(model))
            throw new RecognitionUnavailableException("Не задана модель Ollama.");

        // PDF → PNG-страницы (Ollama не принимает PDF). Картинки идут как есть.
        string[] images;
        if (RecognitionShared.ImageTypes.Contains(mimeType))
        {
            images = [Convert.ToBase64String(file)];
        }
        else if (mimeType.Equals(PdfMime, StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<byte[]> pages;
            try
            {
                pages = await Task.Run(() => PdfRasterizer.ToPngPages(file), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new RecognitionUnavailableException($"Ollama: не удалось конвертировать PDF в изображения: {ex.Message}");
            }
            if (pages.Count == 0)
                throw new RecognitionUnavailableException("Ollama: PDF не содержит страниц для распознавания.");
            logger.LogInformation("Ollama: PDF растеризован в {N} стр. @ {Dpi} DPI", pages.Count, PdfRasterizer.DefaultDpi);
            images = pages.Select(Convert.ToBase64String).ToArray();
        }
        else
        {
            throw new RecognitionUnavailableException($"Ollama: неподдерживаемый тип «{mimeType}» (нужны изображения или PDF).");
        }

        var baseUrl = string.IsNullOrWhiteSpace(cfg.BaseUrl) ? "http://localhost:11434" : cfg.BaseUrl;

        // Контекст по умолчанию (4096) мал для vision: одно изображение ~4–5 тыс. токенов.
        // Оцениваем: промпт + ~4608 токенов на страницу; клампим в разумные пределы.
        int numCtx = Math.Clamp(2048 + images.Length * 4608, 8192, 32768);

        // НЕ используем format:"json" (issue #318): у thinking-моделей (qwen3-vl) JSON-грамматика
        // глушит основной вывод — размышления уходят в отдельное поле `thinking`, а `response`
        // приходит ПУСТЫМ. Без format модель отдаёт чистый JSON в `response` (инструкция «только JSON»
        // есть в промпте), а RecognitionShared.ParseValues извлекает JSON устойчиво. Non-thinking
        // модели (qwen2.5vl) работают одинаково с format и без.
        var requestBody = new
        {
            model,
            prompt = (promptBuilder ?? RecognitionShared.BuildPrompt)(fields),
            images,
            stream = false,
            think = false, // подсказка не размышлять (thinking-модели могут игнорировать — тогда спасает парсер)
            options = new { temperature = 0, num_ctx = numCtx },
        };
        var json = JsonSerializer.Serialize(requestBody);

        HttpResponseMessage resp;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/generate")
            { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            resp = await http.SendAsync(req, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new RecognitionUnavailableException($"Ollama недоступен ({baseUrl}): {ex.Message}");
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning("Ollama {Status}: {Body}", resp.StatusCode, RecognitionShared.Truncate(body, 300));
            throw new RecognitionUnavailableException($"Ollama ответил {(int)resp.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("response", out var r) ? r.GetString() ?? "" : "";
    }
}
