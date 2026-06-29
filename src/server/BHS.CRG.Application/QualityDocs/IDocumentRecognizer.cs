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
    Task<RecognitionResult> RecognizeAsync(
        byte[] file, string mimeType, IReadOnlyList<RecognitionField> fields, CancellationToken ct = default);
}

/// <summary>Превышен лимит запросов к LLM — следует остановиться до восстановления.</summary>
public class RecognitionLimitException(string message, int? retryAfterSeconds = null) : Exception(message)
{
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>Распознаватель не настроен (нет API-ключа) или иная ошибка конфигурации.</summary>
public class RecognitionUnavailableException(string message) : Exception(message);
