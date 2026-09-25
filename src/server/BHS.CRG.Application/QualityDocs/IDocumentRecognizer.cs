namespace BHS.CRG.Application.QualityDocs;

/// <summary>Поле-цель для извлечения (плоский путь, напр. «ВыпустившаяОрганизация.ИНН»).</summary>
public record RecognitionField(string Path, string Title, string Type, IReadOnlyList<string>? Options = null);

/// <summary>
/// Результат распознавания: значения по плоским путям полей + сырой текст (для отладки)
/// + число страниц скана (для автозаполнения поля с тэгом doc.pageCount).
/// </summary>
/// <param name="Engine">
/// Кто распознал — имя движка и модель («Ollama · qwen2.5vl:7b»), для уведомления о результате
/// (issue #803). Вопрос «почему у меня плохо распозналось» задают ПОСЛЕ прогона, и ответ на него
/// начинается с того, кем он делался; выбор движка при этом знает только цепочка.
/// </param>
public record RecognitionResult(IReadOnlyDictionary<string, string?> Values, string? RawText, int? PageCount = null,
    string? Engine = null);

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

/// <summary>
/// Текст отказа движка распознавания или веб-поиска — НАШ, и уходит он наружу дословно (issue #1050).
///
/// <para>Зачем отдельная форма. Правило #691 различает свой отказ и чужой ПО ТИПУ, и типы этого
/// семейства (<see cref="RecognitionUnavailableException" /> и соседи) доменными не являются:
/// конвейер ответа их текст наружу не пускает. Но пускают два других выхода — ответ эндпоинта
/// библиотеки качества и отказ распознавания наборов данных, — и там текст нужен целиком: «Не задан
/// ключ Gemini», «достигнут лимит запросов», «ответ не поместился в лимит и оборван» — это то, ради
/// чего человек вообще открыл экран. Заменить их общей фразой значит оставить его без причины.</para>
///
/// <para>Писать <c>ex.Message</c> в таких местах руками нельзя: по виду это ровно та строка, которую
/// правило запрещает, и отличить её от настоящей утечки не сможет ни ревью, ни сторож. Поэтому
/// форма названа. Её обещание держится не словом, а проверкой: <c>RefusalTextTests</c> следит, чтобы
/// тексты этого семейства собирались только из литералов, наших же настроек и разбора
/// <c>OutboundDiagnosis</c> — и не вбирали ни чужого сообщения, ни тела чужого ответа.</para>
/// </summary>
public static class EngineRefusal
{
    /// <param name="ex">Отказ движка: его сообщение написано нами и адресовано человеку.</param>
    public static string TextOf(Exception ex) => ex.Message;
}

/// <summary>Превышен лимит запросов к LLM — следует остановиться до восстановления.</summary>
public class RecognitionLimitException(string message, int? retryAfterSeconds = null) : Exception(message)
{
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>Распознаватель не настроен (нет API-ключа) или иная ошибка конфигурации.</summary>
public class RecognitionUnavailableException(string message) : Exception(message);

/// <summary>
/// Движок ответил, но ответа в ответе не было (issue #802): пустой текст, обрезанный на середине
/// JSON, проза вместо данных. «Не смог» и «промолчал» — разные вещи, и раньше вторая была
/// невыразима: пустая строка возвращалась как успех, разбор отдавал пустой словарь, задача
/// завершалась `Succeeded` — и ноль полей выглядел законным результатом распознавания.
///
/// Наследник <see cref="RecognitionUnavailableException" /> по тому же правилу, что и таймаут:
/// цепочке этого достаточно, чтобы перейти к следующему движку, и ни одно место, ловящее базовый
/// тип, о новом знать не обязано.
///
/// Отдельный тип нужен ровно там, где разница ЕСТЬ, — в постраничном прогоне. Молчание страничное:
/// три пустых листа из шестнадцати не значат, что движок негоден, поэтому молчание на ПЕРВОЙ
/// странице прогон не роняет (в отличие от таймаута, который его прекращает). Обратная страховка —
/// счётчик подряд идущих молчаний, чтобы не потратить часы на движок, который не отвечает вовсе.
/// </summary>
public class RecognitionSilentException(string message) : RecognitionUnavailableException(message);

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

/// <summary>
/// Поставщик ответил, что такой модели у него нет (issue #923) — настоящий 404 на генерацию.
///
/// Наследник <see cref="RecognitionUnavailableException" /> по тому же правилу, что таймаут и
/// молчание: цепочке этого достаточно, чтобы перейти к следующему движку. Отдельный тип нужен
/// цепочке, чтобы передать наблюдение каталогу моделей: до этого снятие модели узнавалось только
/// плановой платной пробой, а бесплатный и точный отказ распознавания пропадал.
///
/// Модель — та, с которой движок РЕАЛЬНО обращался (с учётом умолчания), а не прочитанная заново из
/// настроек: их могли сменить, пока запрос шёл.
/// </summary>
public class RecognitionModelGoneException(string engine, string model, string? advice, string message)
    : RecognitionUnavailableException(message)
{
    public string Engine { get; } = engine;
    public string Model { get; } = model;
    /// <summary>Совет поставщика из текста отказа («поставщик рекомендует …»), если он есть.</summary>
    public string? Advice { get; } = advice;
}
