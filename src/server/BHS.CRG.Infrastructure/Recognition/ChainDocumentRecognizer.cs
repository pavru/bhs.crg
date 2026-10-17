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
///
/// <para>Файл, который не читается, к движкам не уходит вовсе (issue #1343).</para>
/// </summary>
public class ChainDocumentRecognizer(
    RecognitionEngineSelector selector, ILogger<ChainDocumentRecognizer> logger
) : IDocumentRecognizer
{
    public async Task<RecognitionResult> RecognizeAsync(byte[] file, string mimeType, IReadOnlyList<RecognitionField> fields,
        Func<IReadOnlyList<RecognitionField>, string>? promptBuilder = null, CancellationToken ct = default)
    {
        // Файл — ПЕРВЫМ, до выбора движков: проверка бесплатна, а выбор бывает с пробой зрения. И слова
        // о файле верны при любых настройках, а «распознавать некому» — нет, если файл всё равно не читается.
        var check = await RecognitionFileCheck.CheckAsync(file, mimeType, ct);
        if (check.Why is { } unreadable)
        {
            logger.LogWarning(check.Cause, "Распознавание: файл не читается и к движкам не отправлен");
            throw new RecognitionFileRejectedException(unreadable);
        }
        if (check.Cause is { } skipped)
            // Сбой окружения, а не файла: молчать о нём нельзя, отказывать файлу — тоже.
            logger.LogWarning(skipped, "Распознавание: файл проверить не удалось — отправлен движкам как есть");

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
                return new RecognitionResult(values, text, Engine: await DescribeAsync(engine, ct));
            }
            catch (RecognitionLimitException ex) { logger.LogWarning("Движок {Engine}: лимит — следующий. {Msg}", engine.Name, ex.Message); last = Keep(last, ex); }
            catch (RecognitionModelGoneException ex)
            {
                // Бесплатный и точный сигнал: без передачи каталогу снятие модели узнавалось бы только
                // плановой платной пробой, раз в часы (issue #923).
                logger.LogWarning("Движок {Engine}: модель снята — следующий. {Msg}", engine.Name, ex.Message);
                await selector.ObserveGoneAsync(ex, ct);
                last = Keep(last, ex);
            }
            catch (RecognitionUnavailableException ex) { logger.LogWarning("Движок {Engine}: недоступен — следующий. {Msg}", engine.Name, ex.Message); last = Keep(last, ex); }
        }
        throw last ?? new RecognitionUnavailableException("Распознавание не удалось ни одним движком.");
    }

    /// <summary>
    /// Кем распозналось — «Ollama · qwen2.5vl:7b». Модель берём из настроек: без неё имя движка
    /// отвечает лишь на половину вопроса, а разбираются обычно именно с моделью.
    /// </summary>
    private async Task<string> DescribeAsync(IRecognizerEngine engine, CancellationToken ct)
    {
        var model = (await selector.ModelOfAsync(engine.Name, ct));
        return string.IsNullOrWhiteSpace(model) ? engine.Name : $"{engine.Name} · {model}";
    }

    /// <summary>
    /// Какую из неудач показать человеку. Раньше побеждала последняя по порядку, то есть при
    /// «Gemini исчерпал лимит, Ollama промолчала» наверх уезжало молчание Ollama — а починить нужно
    /// лимит. Лимит важнее прочего: он называет причину, которая пройдёт сама, и срок.
    /// </summary>
    private static Exception Keep(Exception? previous, Exception current)
        => previous is RecognitionLimitException ? previous : current;
}
