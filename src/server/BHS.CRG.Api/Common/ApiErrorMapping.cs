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

        // Всё прочее — внутренности: строка подключения Npgsql с хостом и именем БД, адреса
        // хранилища, stderr компилятора с путями, куски ответов сторонних API. Наружу отдаём только
        // идентификатор запроса — по нему администратор находит запись в логе.
        return (StatusCodes.Status500InternalServerError,
            $"Внутренняя ошибка сервера. Идентификатор запроса: {traceId}");
    }

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
