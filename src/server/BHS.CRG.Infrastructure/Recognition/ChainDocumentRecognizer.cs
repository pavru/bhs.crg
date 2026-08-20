using BHS.CRG.Application.QualityDocs;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>
/// Распознаватель-цепочка: порядок и доступность движков берутся из настроек интеграций
/// (enable/disable + приоритет). Использует первый включённый, настроенный и зрячий; при
/// недоступности/лимите переходит к следующему.
///
/// Отбор движков живёт не здесь, а в <see cref="RecognitionEngineSelector" />: тем же правилом
/// пользуется предполётная проверка, и разъехаться им нельзя (issue #801).
/// </summary>
public class ChainDocumentRecognizer(
    RecognitionEngineSelector selector, ILogger<ChainDocumentRecognizer> logger
) : IDocumentRecognizer
{
    public async Task<RecognitionResult> RecognizeAsync(byte[] file, string mimeType, IReadOnlyList<RecognitionField> fields,
        Func<IReadOnlyList<RecognitionField>, string>? promptBuilder = null, CancellationToken ct = default)
    {
        var selection = await selector.SelectAsync(probeVision: true, ct);
        var ordered = selection.Ordered;

        if (ordered.Count == 0)
            throw new RecognitionUnavailableException(selection.Blind.Count > 0
                // Слепой движок мы не «пропускаем молча»: если работать больше некому, человек
                // обязан узнать ИМЕННО про слепоту — иначе он пойдёт проверять ключи и настройки,
                // которые в полном порядке.
                ? string.Join(" ", selection.Blind.Select(b => b.Issue))
                : "Нет включённых и настроенных движков распознавания. Проверьте «Настройки → Поиск и распознавание».");

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
            catch (RecognitionLimitException ex) { logger.LogWarning("Движок {Engine}: лимит — следующий. {Msg}", engine.Name, ex.Message); last = Keep(last, ex); }
            catch (RecognitionUnavailableException ex) { logger.LogWarning("Движок {Engine}: недоступен — следующий. {Msg}", engine.Name, ex.Message); last = Keep(last, ex); }
        }
        throw last ?? new RecognitionUnavailableException("Распознавание не удалось ни одним движком.");
    }

    /// <summary>
    /// Какую из неудач показать человеку. Раньше побеждала последняя по порядку, то есть при
    /// «Gemini исчерпал лимит, Ollama промолчала» наверх уезжало молчание Ollama — а починить нужно
    /// лимит. Лимит важнее прочего: он называет причину, которая пройдёт сама, и срок.
    /// </summary>
    private static Exception Keep(Exception? previous, Exception current)
        => previous is RecognitionLimitException ? previous : current;
}
