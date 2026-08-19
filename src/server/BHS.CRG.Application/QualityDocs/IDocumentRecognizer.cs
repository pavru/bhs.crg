namespace BHS.CRG.Application.QualityDocs;

/// <summary>Поле-цель для извлечения (плоский путь, напр. «ВыпустившаяОрганизация.ИНН»).</summary>
public record RecognitionField(string Path, string Title, string Type, IReadOnlyList<string>? Options = null);

/// <summary>
/// Результат распознавания: значения по плоским путям полей + сырой текст (для отладки)
/// + число страниц скана (для автозаполнения поля с тэгом doc.pageCount).
/// </summary>
public record RecognitionResult(IReadOnlyDictionary<string, string?> Values, string? RawText, int? PageCount = null);

/// <summary>
/// Извлекает реквизиты документа из скан-копии (image/pdf) по заданному списку полей.
/// Реализуется через vision-LLM.
/// </summary>
public interface IDocumentRecognizer
{
    /// <param name="promptBuilder">Необязательный кастомный промпт (по умолчанию —
    /// RecognitionShared.BuildPrompt, формулировка под сертификаты/декларации). Например,
    /// RecognitionShared.BuildTitleBlockPrompt для распознавания штампа чертежа по ГОСТ.</param>
    Task<RecognitionResult> RecognizeAsync(
        byte[] file, string mimeType, IReadOnlyList<RecognitionField> fields,
        Func<IReadOnlyList<RecognitionField>, string>? promptBuilder = null, CancellationToken ct = default);
}

/// <summary>Превышен лимит запросов к LLM — следует остановиться до восстановления.</summary>
public class RecognitionLimitException(string message, int? retryAfterSeconds = null) : Exception(message)
{
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>Распознаватель не настроен (нет API-ключа) или иная ошибка конфигурации.</summary>
public class RecognitionUnavailableException(string message) : Exception(message);

/// <summary>
/// Движок не ответил за отведённый срок (issue #797).
///
/// Наследник <see cref="RecognitionUnavailableException" /> намеренно: для цепочки движков это
/// обычная недоступность — перебор идёт дальше, и ни одному месту, которое ловит базовый тип, не
/// нужно знать о новом. Отдельный тип нужен там, где разница ЕСТЬ: постраничные прогоны отличают
/// «этот движок вообще не работает» (прекращать) от «одна страница не уложилась в срок»
/// (пропустить страницу и продолжить) — раньше эту роль случайно играл сырой
/// <see cref="TaskCanceledException" />, и классификация таймаута отобрала бы её, не дав замены.
/// </summary>
public class RecognitionTimeoutException(string message) : RecognitionUnavailableException(message);
