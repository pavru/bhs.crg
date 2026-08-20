using System.Text;
using System.Text.Json;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Http;
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

    /// <summary>
    /// Срок ответа. Он ЗАВЕДОМО больше облачных: модель считается на этой же машине, часто на CPU, и
    /// vision-проход по странице там измеряется минутами, а не секундами. Пять минут — не «на всякий
    /// случай», а замеренный порядок: в сравнении движков локальные модели отставали от облачных
    /// вдесятеро. Ставить им общие две минуты значило бы объявлять локальное распознавание сломанным
    /// на машинах без видеокарты.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Сколько токенов контекста просим у модели в самом большом случае — вход плюс резерв под ответ.
    /// Ограничение НАШЕ, а не модельное: qwen3-vl держит кратно больше. Верхняя граница нужна не
    /// модели, а машине — контекст лежит в памяти, и на машине без видеокарты щедрость здесь
    /// оплачивается временем и подкачкой. Поэтому и в тексте отказа предел назван отведённым: выбрать
    /// это число за пользователя, не замерив на его железе, мы не можем, а соврать про виновника —
    /// значит отправить его менять модель, которая ни при чём.
    /// </summary>
    private const int MaxContextTokens = 32768;

    /// <summary>
    /// Потолок ответа (<c>num_predict</c>). Задан явно по той же причине, что у облачных, и с
    /// дополнительной: умолчание Ollama исторически бывало равно 128 токенам — на таком ответе не
    /// помещается даже штамп, а выглядело бы это как «модель плохо распознаёт».
    ///
    /// Восемь тысяч — по расчёту таблицы (строка 40–60 токенов, сотня строк около шести тысяч) и по
    /// тому, сколько модели физически есть где написать: у Ollama ответ считается ВНУТРИ контекста,
    /// и потолок больше отведённого в <c>num_ctx</c> резерва — фикция. Замеры 2026-08-20 на qwen3-vl,
    /// три точки:
    ///
    /// <list type="bullet">
    /// <item>num_predict 120 → израсходовано 120, ответ ПУСТ, 339 символов размышлений;</item>
    /// <item>num_predict 4096 → израсходовано 4096, ответ ПУСТ, 7349 символов размышлений;</item>
    /// <item>num_predict 16384 при num_ctx 8192 → израсходовано 8135 (упёрлось в КОНТЕКСТ, не в
    /// потолок), ответ ПУСТ, 10453 символа размышлений, 204 секунды.</item>
    /// </list>
    ///
    /// Отсюда два вывода, и оба неочевидны. Первый: размышления тратят тот же счёт, что и ответ, —
    /// значит «локальная пишет медленно, дадим ей поменьше» есть довод против самих данных, экономия
    /// оборачивается не коротким ответом, а его отсутствием. Второй: думающая модель РАСШИРЯЕТ
    /// рассуждение под выданный бюджет (339 → 7349 → 10453 символов), поэтому увеличением потолка
    /// такую модель не вылечить — на любом числе она рассуждает ровно до конца.
    ///
    /// Что здесь действительно нас защищает — не размер, а громкость: <c>done_reason: length</c>
    /// превращается в отказ, и выдумка не попадает в документацию. Выбор модели, которая доходит до
    /// ответа, — вопрос настроек, а не кода.
    /// </summary>
    public const int MaxOutputTokens = 8192;

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
        // Оцениваем: промпт + ~4608 токенов на страницу.
        //
        // В счёт входит и ОТВЕТ — замер 2026-08-20 показал это прямо: при num_predict = 16384 и
        // num_ctx = 8192 генерация оборвалась на 8135 токенах с done_reason: length, то есть уперлась
        // в контекст, а не в потолок ответа. Поэтому резерв под ответ добавляется ПОВЕРХ ограничения
        // входа, а не внутрь него: сложив их до клампа, мы бы отдавали резерв обратно ровно на
        // многостраничных вызовах — а это счёт целиком и таблица документа, то есть те самые, кому
        // нужен самый длинный ответ.
        var inputTokens = 2048 + images.Length * 4608;
        if (inputTokens + MaxOutputTokens > MaxContextTokens)
            // Вход, который не помещается, раньше молча обрезался: модель получала неполный документ
            // и отвечала по нему, ничем не выдавая потери. Отказ вместо этого — тот же принцип, что
            // и у обрезанного ответа (issue #802).
            // «В отведённый», а не «в контекст модели»: предел ЗДЕСЬ наш, а не модельный — qwen3-vl
            // держит кратно больше. Сказав «модель не может», мы отправили бы человека искать другую,
            // а та упёрлась бы в то же самое число, потому что число наше.
            throw new RecognitionUnavailableException(
                $"Ollama: {images.Length} листов в один вызов не помещаются в отведённый контекст " +
                $"({MaxContextTokens} токенов) — распознавайте документ частями либо облачным движком.");
        var numCtx = Math.Max(8192, inputTokens + MaxOutputTokens);

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
            options = new { temperature = 0, num_ctx = numCtx, num_predict = MaxOutputTokens },
        };
        var json = JsonSerializer.Serialize(requestBody);

        HttpResponseMessage resp;
        string body;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/generate")
            { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            resp = await http.SendAsync(req, ct);
            // Чтение тела — внутри того же try: HttpClient.Timeout отмеряет и его, а снаружи таймаут
            // снова стал бы голой отменой, мимо всей классификации (issue #797).
            body = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (HttpFailure.IsTimeout(ex, ct))
        {
            logger.LogWarning("Ollama ({BaseUrl}) не ответил за {Timeout}", baseUrl, HttpFailure.Format(Timeout));
            throw new RecognitionTimeoutException($"Ollama: не ответил за {HttpFailure.Format(Timeout)}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new RecognitionUnavailableException($"Ollama недоступен ({baseUrl}): {ex.Message}");
        }

        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning("Ollama {Status}: {Body}", resp.StatusCode, RecognitionShared.Truncate(body, 300));
            throw new RecognitionUnavailableException($"Ollama ответил {(int)resp.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(body);
        // Ответ, упёршийся в num_predict, приходит обрезанным на середине — и до issue #802 это
        // выглядело как «модель так ответила». Признак у Ollama свой: done_reason = length.
        if (doc.RootElement.TryGetProperty("done_reason", out var done) && done.GetString() == "length")
            throw new RecognitionSilentException(
                $"Ollama: ответ не поместился в лимит ({MaxOutputTokens} токенов) и оборван на середине — " +
                "данные пришли неполными.");

        var text = doc.RootElement.TryGetProperty("response", out var r) ? r.GetString() : null;
        if (string.IsNullOrWhiteSpace(text))
            // Пустой ответ наверх не отдаём (issue #802): там он неотличим от «модель ответила, что
            // полей нет». У Ollama это в первую очередь thinking-модели — содержательный текст ушёл
            // в отдельное поле; доставать его оттуда будем в #803, но молчать об этом нельзя уже
            // сейчас.
            throw new RecognitionSilentException("Ollama: модель вернула пустой ответ.");
        return text;
    }
}
