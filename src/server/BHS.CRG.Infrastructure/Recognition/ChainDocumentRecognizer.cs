using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Settings;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>
/// Распознаватель-цепочка: порядок и доступность движков берутся из настроек интеграций
/// (enable/disable + приоритет). Использует первый включённый и настроенный; при
/// недоступности/лимите переходит к следующему.
/// </summary>
public class ChainDocumentRecognizer(
    IEnumerable<IRecognizerEngine> engines, IIntegrationSettings settings, ILogger<ChainDocumentRecognizer> logger
) : IDocumentRecognizer
{
    public async Task<RecognitionResult> RecognizeAsync(byte[] file, string mimeType, IReadOnlyList<RecognitionField> fields,
        Func<IReadOnlyList<RecognitionField>, string>? promptBuilder = null, CancellationToken ct = default)
    {
        var s = await settings.GetEffectiveAsync(ct);
        var byName = engines.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);

        // порядок из настроек; затем движки не упомянутые в порядке
        var order = s.RecognitionOrder.Count > 0 ? s.RecognitionOrder : ["Gemini", "Anthropic", "Ollama"];
        var ordered = order.Where(byName.ContainsKey)
            .Concat(byName.Keys.Where(n => !order.Contains(n, StringComparer.OrdinalIgnoreCase)))
            .Select(n => byName[n])
            .ToList();

        // Движок с галкой «включён», но без ключа/модели из перебора выпадает — пишем об этом в лог
        // поимённо. Молча пропущенный движок выглядел в настройках работающим, и понять, почему
        // распознаёт не он, было неоткуда (issue #797); в UI то же самое видно бейджем.
        foreach (var e in ordered.Where(e => s.Rec(e.Name).Enabled))
            if (EngineReadiness.MissingForRecognition(e.Name, s.Rec(e.Name)) is { } missing)
                logger.LogWarning("Движок {Engine} включён, но не участвует: {Missing}", e.Name, missing);

        ordered = ordered.Where(e => EngineReadiness.IsUsableForRecognition(e.Name, s.Rec(e.Name))).ToList();

        if (ordered.Count == 0)
            throw new RecognitionUnavailableException("Нет включённых и настроенных движков распознавания. Проверьте «Настройки → Поиск и распознавание».");

        Exception? last = null;
        foreach (var engine in ordered)
        {
            try
            {
                var text = await engine.RecognizeRawAsync(file, mimeType, fields, promptBuilder, ct);
                var values = RecognitionShared.ParseValues(text, fields);
                logger.LogInformation("Распознавание выполнено движком {Engine}, полей: {N}", engine.Name, values.Count);
                return new RecognitionResult(values, text);
            }
            catch (RecognitionLimitException ex) { logger.LogWarning("Движок {Engine}: лимит — следующий. {Msg}", engine.Name, ex.Message); last = ex; }
            catch (RecognitionUnavailableException ex) { logger.LogWarning("Движок {Engine}: недоступен — следующий. {Msg}", engine.Name, ex.Message); last = ex; }
        }
        throw last ?? new RecognitionUnavailableException("Распознавание не удалось ни одним движком.");
    }
}
