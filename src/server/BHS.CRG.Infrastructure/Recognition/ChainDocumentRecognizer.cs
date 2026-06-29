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
    public async Task<RecognitionResult> RecognizeAsync(byte[] file, string mimeType, IReadOnlyList<RecognitionField> fields, CancellationToken ct = default)
    {
        var s = await settings.GetEffectiveAsync(ct);
        var byName = engines.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);

        // порядок из настроек; затем движки не упомянутые в порядке
        var order = s.RecognitionOrder.Count > 0 ? s.RecognitionOrder : ["Gemini", "Anthropic", "Ollama"];
        var ordered = order.Where(byName.ContainsKey)
            .Concat(byName.Keys.Where(n => !order.Contains(n, StringComparer.OrdinalIgnoreCase)))
            .Select(n => byName[n])
            .Where(e => IsUsable(e.Name, s))
            .ToList();

        if (ordered.Count == 0)
            throw new RecognitionUnavailableException("Нет включённых и настроенных движков распознавания. Проверьте «Настройки → Поиск и распознавание».");

        Exception? last = null;
        foreach (var engine in ordered)
        {
            try
            {
                var text = await engine.RecognizeRawAsync(file, mimeType, fields, ct);
                var values = RecognitionShared.ParseValues(text, fields);
                logger.LogInformation("Распознавание выполнено движком {Engine}, полей: {N}", engine.Name, values.Count);
                return new RecognitionResult(values, text);
            }
            catch (RecognitionLimitException ex) { logger.LogWarning("Движок {Engine}: лимит — следующий. {Msg}", engine.Name, ex.Message); last = ex; }
            catch (RecognitionUnavailableException ex) { logger.LogWarning("Движок {Engine}: недоступен — следующий. {Msg}", engine.Name, ex.Message); last = ex; }
        }
        throw last ?? new RecognitionUnavailableException("Распознавание не удалось ни одним движком.");
    }

    private static bool IsUsable(string name, IntegrationSettingsModel s)
    {
        var e = s.Rec(name);
        if (!e.Enabled) return false;
        return name.Equals("Ollama", StringComparison.OrdinalIgnoreCase)
            ? !string.IsNullOrWhiteSpace(e.Model)
            : !string.IsNullOrWhiteSpace(e.ApiKey);
    }
}
