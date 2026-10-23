namespace BHS.CRG.Api.Common;

/// <summary>
/// Что ответить на необработанное исключение: код и текст для клиента (issue #691).
///
/// Правило одно и оно обратное прежнему: <b>дословно наружу уходит только текст
/// <see cref="DomainException" /></b> — отказа, который сформулировали мы и адресовали
/// пользователю. Всё остальное получает обобщённый ответ с идентификатором запроса, а подробности
/// уходят в лог.
///
/// Раньше признаком «наш отказ» служил ТИП: <c>ArgumentException</c> → 400 с текстом,
/// <c>KeyNotFoundException</c> → 404 с текстом. Но ровно эти типы бросают и Npgsql, и SDK
/// хранилища, и Jint, и разбор CIDR — и тогда клиенту уезжало сообщение, называющее хост, базу,
/// бакет или имя внутреннего параметра. Отличить их было нечем: тип один и тот же.
///
/// Вынесено из <c>Program.cs</c> отдельным классом не ради стройности, а чтобы правило можно было
/// проверить тестами: там оно жило внутри лямбды конвейера и проверялось только глазами.
/// </summary>
public static class ApiErrorMapping
{
    /// <summary>Ответ на исключение. <paramref name="traceId" /> попадает в текст только у 500.</summary>
    public static (int Status, string Message) Describe(Exception? ex, string traceId)
    {
        // Тело больше потолка приходит как BadHttpRequestException(413) и без этой ветки уезжало бы
        // в 500 с английским текстом фреймворка (issue #482).
        if (ex is Microsoft.AspNetCore.Http.BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
            // Без числа и без слова «файл»: сюда попадают запросы к разным эндпоинтам с разными
            // пределами, и тело может вообще не быть файлом. Назвать чужой предел — хуже, чем не назвать.
            return (StatusCodes.Status413PayloadTooLarge, "Запрос превышает допустимый размер.");

        // Наш отказ: текст написан для пользователя и доходит до него как есть.
        if (ex is DomainException domain)
            return (StatusOf(domain), domain.Message);

        // Запись проиграла одновременной (версия строки разошлась, ревью PR #1580). Человеку надо
        // перечитать и повторить, а не звать администратора, — поэтому 409, а не 500. Одним местом на
        // ВСЕ контексты, ядра и модулей: контекст, забывший перевести исключение сам, иначе отвечал бы
        // «внутренней ошибкой». Текст общий и ничего о базе не называет; контекст, которому нужны свои
        // слова («счёт изменили…»), переводит исключение в ConflictException сам — как CostsDbContext.
        if (ex is Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
            return (StatusCodes.Status409Conflict, ConcurrentWriteMessage);

        // Всё прочее — внутренности: строка подключения Npgsql с хостом и именем БД, адреса
        // хранилища, stderr компилятора с путями, куски ответов сторонних API. Наружу отдаём только
        // идентификатор запроса — по нему администратор находит запись в логе.
        return (StatusCodes.Status500InternalServerError,
            $"Внутренняя ошибка сервера. Идентификатор запроса: {traceId}");
    }

    /// <summary>Ответ на запись, проигравшую одновременной, — у контекста, не назвавшего своих слов.</summary>
    public const string ConcurrentWriteMessage =
        "Запись изменили одновременно с этой правкой, и она не записана — целиком, ни одной частью. " +
        "Перечитайте её и повторите: записанная поверх, она затёрла бы чужую правку.";

    /// <summary>
    /// Места, на которые указывает отказ, — если он их называет (issue #957). Спрашиваем ИНТЕРФЕЙС,
    /// а не конкретный тип: конвейеру ответа незачем знать охрану записи поимённо.
    /// </summary>
    public static IReadOnlyList<RefusalDetail>? DetailsOf(Exception? ex) =>
        ex is IDetailedRefusal detailed && detailed.Details.Count > 0 ? detailed.Details : null;

    /// <summary>Код ответа по РОДУ отказа. Domain про HTTP не знает — соответствие живёт здесь.</summary>
    public static int StatusOf(DomainException ex) => ex switch
    {
        NotFoundException => StatusCodes.Status404NotFound,
        ConflictException => StatusCodes.Status409Conflict,
        ForbiddenException => StatusCodes.Status403Forbidden,
        InvalidRequestException => StatusCodes.Status400BadRequest,
        // Новый род отказа без своей строки здесь — это 400: запрос отвергнут, но чем именно
        // ответить, никто не сказал. Молчаливый 500 был бы хуже — он прячет наш же текст.
        _ => StatusCodes.Status400BadRequest,
    };
}
